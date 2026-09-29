using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using AlphacamBridge.ComInterop;
using AlphacamBridge.Core.Configuration;
using AlphacamBridge.Core.Protocol;
using AlphacamBridge.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AlphacamBridge;

// ────────────────────────────────────────────────────────────────
//  COM Interface — Alphacam calls these methods on the add-in
// ────────────────────────────────────────────────────────────────

/// <summary>
/// COM-visible interface that Alphacam expects on a DLL add-in.
/// Alphacam discovers and calls these methods by name via IDispatch.
/// </summary>
[ComVisible(true)]
[Guid("B4A7C3E1-9F2D-4E8A-B5C6-1A2B3C4D5E6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface IAlphacamBridgeAddin
{
    [DispId(1)]
    int InitAlphacamAddIn(int acamVersion);

    [DispId(2)]
    void BeforeUnload();
}

// ────────────────────────────────────────────────────────────────
//  COM Class — The add-in implementation
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Alphacam Bridge add-in. Loaded by Alphacam at startup via COM.
/// Starts a named-pipe JSON-RPC server that exposes Alphacam's API
/// to external clients (e.g. an MCP server for Claude Desktop).
/// </summary>
[ComVisible(true)]
[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890")]
[ClassInterface(ClassInterfaceType.None)]
[ProgId("AlphacamBridge.Addin")]
public sealed class AlphacamBridgeAddin : IAlphacamBridgeAddin
{
    // ── Infrastructure ──────────────────────────────────────────
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private CancellationTokenSource? _cts;

    // ── Bridge components ───────────────────────────────────────
    private PipeServer? _pipeServer;
    private StaThreadDispatcher? _staDispatcher;
    private AlphacamConnection? _connection;
    private ShimPipeClient? _shimClient;

    // ── Alphacam state ──────────────────────────────────────────
    private int _acamVersion;

    // ────────────────────────────────────────────────────────────
    //  InitAlphacamAddIn — called by Alphacam on startup
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by Alphacam when the add-in is loaded (at startup or via Utils > Add-Ins).
    /// The return value should be 0.
    /// </summary>
    public int InitAlphacamAddIn(int acamVersion)
    {
        // Breadcrumb file for diagnosing COM activation — writes to the DLL directory
        var breadcrumbDir = Path.GetDirectoryName(typeof(AlphacamBridgeAddin).Assembly.Location)
                            ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var breadcrumbPath = Path.Combine(breadcrumbDir, "alphacam-bridge-loaded.txt");

        try
        {
            File.AppendAllText(breadcrumbPath,
                $"[{DateTime.Now:O}] InitAlphacamAddIn called, acamVersion={acamVersion}{Environment.NewLine}");
        }
        catch { /* Can't write breadcrumb — non-fatal */ }

        _acamVersion = acamVersion;

        try
        {
            Initialize();

            try { File.AppendAllText(breadcrumbPath, $"[{DateTime.Now:O}] Initialize succeeded{Environment.NewLine}"); }
            catch { }

            return 0; // Success
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(breadcrumbPath, $"[{DateTime.Now:O}] Initialize FAILED: {ex}{Environment.NewLine}"); }
            catch { }

            System.Diagnostics.Debug.WriteLine($"AlphacamBridge: InitAlphacamAddIn failed: {ex}");

            // Try to log if logger is available
            try { _logger?.LogCritical(ex, "Failed to initialize Alphacam Bridge add-in"); }
            catch { /* Swallow — can't log */ }

            return -1; // Signal failure but don't crash Alphacam
        }
    }

    /// <summary>
    /// Called by Alphacam before the add-in is unloaded (Utils > Add-Ins uncheck, or Alphacam exit).
    /// </summary>
    public void BeforeUnload()
    {
        try
        {
            Shutdown();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AlphacamBridge: BeforeUnload error: {ex}");
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Initialization
    // ────────────────────────────────────────────────────────────

    private void Initialize()
    {
        // 1. Load configuration
        var configPath = ResolveConfigPath();
        BridgeConfiguration config;

        if (File.Exists(configPath))
        {
            config = BridgeConfiguration.LoadFromFile(configPath);
        }
        else
        {
            // Use defaults and save a sample config for the user
            config = BridgeConfiguration.CreateSample();
            try
            {
                var configDir = Path.GetDirectoryName(configPath);
                if (configDir != null && !Directory.Exists(configDir))
                    Directory.CreateDirectory(configDir);
                config.SaveToFile(configPath);
            }
            catch { /* Non-fatal: just use defaults */ }
        }

        // 2. Set up logging (console + event log + file)
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlphacamBridge");
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        var _logFilePath = Path.Combine(logDir, "launcher.log");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .SetMinimumLevel(Enum.Parse<LogLevel>(config.LogLevel, ignoreCase: true))
                .AddSimpleConsole(options =>
                {
                    options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
                    options.SingleLine = true;
                })
                .AddEventLog(settings =>
                {
                    settings.SourceName = "AlphacamBridge";
                    settings.LogName = "Application";
                })
                .AddProvider(new FileLoggerProvider(_logFilePath));
        });

        _logger = _loggerFactory.CreateLogger("AlphacamBridge");
        _logger.LogInformation("Alphacam Bridge add-in initializing (Alphacam version code: {Version})", _acamVersion);

        // 3. Build security components
        var authenticator = BuildAuthenticator(config);
        var securityProvider = new PipeSecurityProvider(config.AllowedPipeSids);
        var pipeSecurity = securityProvider.CreatePipeSecurity();

        // 4. Build COM interop layer
        //    Since we're loaded in-process by Alphacam, we're already on the STA thread.
        //    StaThreadDispatcher marshals calls from the pipe server's async I/O threads
        //    and supports the alphacam.diag diagnostic that calls COM cross-process.
        _staDispatcher = new StaThreadDispatcher(_loggerFactory.CreateLogger<StaThreadDispatcher>());
        _connection = new AlphacamConnection(_staDispatcher, _loggerFactory.CreateLogger<AlphacamConnection>());

        _connection.ConnectionStateChanged += (_, e) =>
        {
            if (e.IsConnected)
                _logger.LogInformation("Connected to Alphacam COM interface");
            else
                _logger.LogWarning("Lost connection to Alphacam COM interface");
        };

        // 5. Build and start pipe server on background thread
        _cts = new CancellationTokenSource();

        _pipeServer = new PipeServer(
            config.PipeName,
            pipeSecurity,
            authenticator,
            config.MaxMessageSizeBytes,
            config.AuthTimeoutSeconds,
            _loggerFactory.CreateLogger<PipeServer>());

        _pipeServer.OnRequestReceived += HandleRequestAsync;

        _logger.LogInformation("Starting pipe server on \\\\.\\pipe\\{PipeName}", config.PipeName);
        _logger.LogInformation("Authentication: {AuthState}",
            config.AuthenticationEnabled ? "ENABLED" : "DISABLED");

        // Start pipe server on a background thread (don't block Alphacam's UI)
        Task.Run(async () =>
        {
            try
            {
                await _pipeServer.StartAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogDebug("Pipe server cancelled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Pipe server error");
            }
        });

        // 6. Add menu item to Alphacam (optional — for status/about)
        try
        {
            AddAlphacamMenuItems();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add menu items (non-fatal)");
        }

        // 7. Acam.exe watchdog — orphan-prevention (see HANDOFF.md, surfaced
        //    2026-06-30). When the user closes Alphacam, the Launcher used to
        //    stay alive holding a dead shim-pipe connection, confusing the
        //    next MCP call. Poll for the Acam process; once we've seen it
        //    running and it disappears, shut down and exit. The _acamSeen
        //    latch keeps a dev-started Launcher (before Alphacam is up) from
        //    exiting immediately.
        Task.Run(() => AcamWatchdogAsync(_cts.Token));

        // 8. Shim-pipe watchdog — the second half of orphan prevention
        //    (found 2026-07-20: this Launcher's ROT proxy keeps the closed
        //    Alphacam alive as a ghost, and if the user has opened a NEW
        //    Alphacam the Acam watchdog never fires because an "Acam"
        //    process still exists). Once we've talked to the shim and its
        //    pipe stays dead for several probes, release the COM proxy
        //    (letting the ghost Acam exit) and shut this Launcher down —
        //    the new Alphacam's shim will start a fresh Launcher.
        Task.Run(() => ShimPipeWatchdogAsync(_cts.Token));

        _logger.LogInformation("Alphacam Bridge add-in initialized successfully");
    }

    // ────────────────────────────────────────────────────────────
    //  Acam.exe watchdog
    // ────────────────────────────────────────────────────────────

    private bool _acamSeen;
    private bool _shimSeen;

    private async Task ShimPipeWatchdogAsync(CancellationToken ct)
    {
        const int probeSeconds = 20;
        const int failuresBeforeExit = 3; // ~1 minute of dead pipe
        int consecutiveFailures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeSeconds), ct);

                if (_shimClient == null) continue; // never talked to the shim yet

                if (_shimClient.IsConnected)
                {
                    _shimSeen = true;
                    consecutiveFailures = 0;
                    continue;
                }

                if (!_shimSeen) continue; // haven't successfully connected yet

                // Pipe was alive once and is down now. Release the ROT proxy
                // IMMEDIATELY — before trying to reconnect — so a closed
                // Alphacam is free to exit even when the user reopens
                // Alphacam fast enough that our reconnect would succeed
                // against the NEW instance's pipe (the fast-reopen race).
                // The proxy lazily reconnects on the next status call, so
                // releasing is always safe.
                if (consecutiveFailures == 0)
                {
                    _logger?.LogInformation("Shim pipe lost — releasing Alphacam COM proxy");
                    try { _connection?.Disconnect(); } catch { }
                }

                // Probe by reconnecting (a NEW Alphacam's shim pipe counts —
                // this Launcher then serves the new instance).
                try
                {
                    _shimClient.Connect();
                    consecutiveFailures = 0;
                    continue;
                }
                catch
                {
                    consecutiveFailures++;
                }

                if (consecutiveFailures >= failuresBeforeExit)
                {
                    _logger?.LogInformation("Shim pipe dead for {Seconds}s — shutting down Launcher", probeSeconds * failuresBeforeExit);
                    try { Shutdown(); } catch { }
                    Environment.Exit(0);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            try { _logger?.LogWarning(ex, "Shim-pipe watchdog error (watchdog stopped)"); } catch { }
        }
    }

    private async Task AcamWatchdogAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct);

                bool acamRunning;
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName("Acam");
                    acamRunning = procs.Length > 0;
                    foreach (var p in procs) p.Dispose();
                }
                catch
                {
                    continue; // Can't enumerate right now — try again next tick
                }

                if (acamRunning)
                {
                    _acamSeen = true;
                    continue;
                }

                if (!_acamSeen) continue; // Alphacam never seen — dev scenario, stay up

                _logger?.LogInformation("Alphacam process (Acam.exe) has exited — shutting down Launcher");
                try { Shutdown(); } catch { /* Best effort */ }
                Environment.Exit(0);
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            try { _logger?.LogWarning(ex, "Acam watchdog error (watchdog stopped)"); } catch { }
        }
    }

    // ────────────────────────────────────────────────────────────
    //  Shutdown
    // ────────────────────────────────────────────────────────────

    private void Shutdown()
    {
        _logger?.LogInformation("Alphacam Bridge add-in shutting down");

        _cts?.Cancel();

        // Give the pipe server a moment to finish
        Thread.Sleep(500);

        if (_pipeServer != null)
        {
            try { _pipeServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
            catch { /* Swallow */ }
        }

        _shimClient?.Dispose();
        _connection?.Disconnect();
        _staDispatcher?.Dispose();
        _loggerFactory?.Dispose();

        _shimClient = null;
        _pipeServer = null;
        _connection = null;
        _staDispatcher = null;
        _loggerFactory = null;
        _logger = null;
    }

    // ────────────────────────────────────────────────────────────
    //  Request handler
    // ────────────────────────────────────────────────────────────

    private async Task<JsonRpcResponse> HandleRequestAsync(
        JsonRpcRequest request, string clientId, string[] allowedScopes)
    {
        // Special method: connection status (Launcher-side, doesn't touch shim pipe)
        // NOTE: alphacam.list_tools is NOT special-cased — it routes to the shim like
        // any other alphacam.* method, where it will eventually list mill tools from
        // the .art library (per MCP tool definition).
        if (request.Method == "alphacam.status")
        {
            // Try to read App.ProgramLetter (read-only COM works cross-process via
            // the ROT proxy). Failure is non-fatal — status should still respond
            // even if Alphacam isn't yet reachable.
            string moduleLetter = null;
            int moduleLetterCode = 0;
            try
            {
                if (_connection != null && _staDispatcher != null)
                {
                    if (!_connection.IsConnected)
                    {
                        try { await _connection.ConnectAsync(); } catch { }
                    }
                    if (_connection.IsConnected)
                    {
                        moduleLetterCode = await _staDispatcher.InvokeAsync(() =>
                        {
                            dynamic app = _connection!.Application;
                            return (int)app.ProgramLetter;
                        });
                        if (moduleLetterCode > 0 && moduleLetterCode < 256)
                            moduleLetter = ((char)moduleLetterCode).ToString();
                    }
                }
            }
            catch { /* swallow — moduleLetter stays null */ }

            var status = new
            {
                Connected = _shimClient?.IsConnected ?? false,
                ShimPipe = "AlphacamBridgeCmd",
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                AlphacamVersion = _acamVersion,
                // Module letter: 'M'=Mill, 'R'=Router, 'L'=Lathe, 'S'=Stone, 'W'=Wire.
                // Surfaced so callers can self-detect the active Alphacam module.
                Module = moduleLetter,
                ModuleCode = moduleLetterCode,
                Mode = "shim-proxy"
            };
            return JsonRpcResponse.Success(request.Id,
                System.Text.Json.JsonSerializer.SerializeToElement(status));
        }

        // Diagnostic: try a COM mutation and return full exception detail
        if (request.Method == "alphacam.diag")
        {
            // Ensure connected first
            if (_connection != null && !_connection.IsConnected)
            {
                try { await _connection.ConnectAsync(); }
                catch (Exception ex)
                {
                    return JsonRpcResponse.Success(request.Id,
                        System.Text.Json.JsonSerializer.SerializeToElement(new
                        {
                            step = "connect",
                            error = ex.ToString()
                        }));
                }
            }

            var results = new List<object>();

            // Test 1: read property (should work)
            try
            {
                var r = await _staDispatcher!.InvokeAsync(() =>
                {
                    dynamic app = _connection!.Application;
                    dynamic drw = app.ActiveDrawing;
                    return (string)drw.FullName;
                });
                results.Add(new { test = "read_FullName", success = true, value = r ?? "(empty)" });
            }
            catch (Exception ex)
            {
                results.Add(new { test = "read_FullName", success = false, error = ex.ToString() });
            }

            // Test 2: call query method (should work)
            try
            {
                var r = await _staDispatcher!.InvokeAsync(() =>
                {
                    dynamic app = _connection!.Application;
                    dynamic drw = app.ActiveDrawing;
                    return (int)drw.GetGeoCount();
                });
                results.Add(new { test = "call_GetGeoCount", success = true, value = r });
            }
            catch (Exception ex)
            {
                results.Add(new { test = "call_GetGeoCount", success = false, error = ex.ToString() });
            }

            // Test 3: call New() (might fail)
            try
            {
                await _staDispatcher!.InvokeAsync(() =>
                {
                    dynamic app = _connection!.Application;
                    app.New();
                });
                results.Add(new { test = "call_New", success = true, value = "completed" });
            }
            catch (Exception ex)
            {
                results.Add(new { test = "call_New", success = false, error = ex.ToString() });
            }

            // Test 4: CreateCircle (might fail)
            try
            {
                await _staDispatcher!.InvokeAsync(() =>
                {
                    dynamic app = _connection!.Application;
                    dynamic drw = app.ActiveDrawing;
                    drw.CreateCircle(2.0, 5.0, 5.0);
                });
                results.Add(new { test = "call_CreateCircle", success = true, value = "completed" });
            }
            catch (Exception ex)
            {
                results.Add(new { test = "call_CreateCircle", success = false, error = ex.ToString() });
            }

            return JsonRpcResponse.Success(request.Id,
                System.Text.Json.JsonSerializer.SerializeToElement(results));
        }

        // Route all alphacam.* tool calls through the shim command pipe
        // (in-process COM execution avoids cross-process RPC_E_SERVERFAULT)
        if (request.Method.StartsWith("alphacam."))
        {
            try
            {
                if (_shimClient == null)
                {
                    _shimClient = new ShimPipeClient(_loggerFactory!.CreateLogger<ShimPipeClient>());
                }

                var responseElement = await _shimClient.SendAsync(
                    request.Id ?? 0, request.Method, request.Params);

                // Check if the shim returned an error
                if (responseElement.TryGetProperty("error", out var errorProp))
                {
                    var code = errorProp.TryGetProperty("code", out var c) ? c.GetInt32() : -32603;
                    var msg = errorProp.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                    return JsonRpcResponse.Failure(request.Id, code, msg);
                }

                // Return the result from the shim
                if (responseElement.TryGetProperty("result", out var resultProp))
                {
                    return JsonRpcResponse.Success(request.Id, resultProp.Clone());
                }

                return JsonRpcResponse.Success(request.Id, JsonSerializer.SerializeToElement(new { }));
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Shim pipe error for {Method}", request.Method);
                return JsonRpcResponse.Failure(request.Id,
                    JsonRpcErrorCode.InternalError,
                    $"Shim pipe error: {ex.Message}");
            }
        }

        // Anything that isn't alphacam.* or alphacam.status/diag is unrecognized.
        return JsonRpcResponse.Failure(request.Id,
            JsonRpcErrorCode.MethodNotFound,
            $"Unknown method: {request.Method}");
    }

    // ────────────────────────────────────────────────────────────
    //  Menu items
    // ────────────────────────────────────────────────────────────

    private void AddAlphacamMenuItems()
    {
        // This would use the Alphacam Frame object to add menu items.
        // For now, we skip this — the bridge works purely via the pipe server.
        // Future: App.Frame.AddMenuItem2("&Bridge Status", "BridgeStatus", acamMenuUTILS, "")
    }

    /// <summary>
    /// Called by Alphacam when the "Bridge Status" menu item is picked.
    /// </summary>
    public void BridgeStatus()
    {
        var connected = _connection?.IsConnected ?? false;
        var pipeRunning = _pipeServer != null;
        var shimConnected = _shimClient?.IsConnected ?? false;

        System.Windows.Forms.MessageBox.Show(
            $"Alphacam Bridge Status\n\n" +
            $"Pipe Server: {(pipeRunning ? "Running" : "Stopped")}\n" +
            $"Shim Pipe: {(shimConnected ? "Connected" : "Idle")}\n" +
            $"Alphacam Connected: {connected}\n" +
            $"Alphacam Version Code: {_acamVersion}",
            "Alphacam Bridge",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Information);
    }

    // ────────────────────────────────────────────────────────────
    //  Configuration helpers
    // ────────────────────────────────────────────────────────────

    private static string ResolveConfigPath()
    {
        // 1. Next to the DLL
        var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var localConfig = Path.Combine(dllDir, "bridge-config.json");
        if (File.Exists(localConfig))
            return localConfig;

        // 2. %LOCALAPPDATA%\AlphacamBridge
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDataConfig = Path.Combine(appData, "AlphacamBridge", "bridge-config.json");
        if (File.Exists(appDataConfig))
            return appDataConfig;

        // 3. Default to appdata location (will be created)
        return appDataConfig;
    }

    private IAuthenticator BuildAuthenticator(BridgeConfiguration config)
    {
        if (!config.AuthenticationEnabled)
            return new NoOpAuthenticator();

        var keyHashes = config.ApiKeys.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value.KeyHash, kv.Value.Scopes));

        return new ApiKeyAuthenticator(keyHashes);
    }

    // All alphacam.* tool dispatch is owned by the shim's ComCommandServer.
    // The Launcher only special-cases alphacam.status and alphacam.diag
    // (both Launcher-side, both touch the ROT-proxied IAlphaCamApp directly).

    // ────────────────────────────────────────────────────────────
    //  COM Registration — writes Alphacam registry keys
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by regsvr32/RegAsm when registering the COM server.
    /// Writes the registry entry that tells Alphacam to load this add-in.
    /// </summary>
    [ComRegisterFunction]
    public static void RegisterAddin(Type t)
    {
        if (t != typeof(AlphacamBridgeAddin))
            return;

        try
        {
            // Alphacam looks for add-in DLLs under this registry path.
            // The key name is the ProgID; the default value is the full DLL path.
            var dllPath = Assembly.GetExecutingAssembly().Location;

            // Try multiple known Alphacam registry roots
            var roots = new[]
            {
                @"SOFTWARE\LicomSystems\Acam\Addins",
                @"SOFTWARE\Hexagon\ALPHACAM\Addins",
            };

            foreach (var root in roots)
            {
                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(root, true);
                    key?.SetValue("AlphacamBridge", dllPath, RegistryValueKind.String);
                }
                catch
                {
                    // Try HKCU if HKLM fails (no admin)
                    try
                    {
                        using var key = Registry.CurrentUser.CreateSubKey(root, true);
                        key?.SetValue("AlphacamBridge", dllPath, RegistryValueKind.String);
                    }
                    catch { /* Silently skip — user can register manually */ }
                }
            }
        }
        catch
        {
            // Non-fatal: the user can manually register via Utils > Add-Ins
        }
    }

    /// <summary>
    /// Called by regsvr32/RegAsm when unregistering the COM server.
    /// Removes the Alphacam registry entries.
    /// </summary>
    [ComUnregisterFunction]
    public static void UnregisterAddin(Type t)
    {
        if (t != typeof(AlphacamBridgeAddin))
            return;

        try
        {
            var roots = new[]
            {
                @"SOFTWARE\LicomSystems\Acam\Addins",
                @"SOFTWARE\Hexagon\ALPHACAM\Addins",
            };

            foreach (var root in roots)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(root, true);
                    key?.DeleteValue("AlphacamBridge", false);
                }
                catch { }
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(root, true);
                    key?.DeleteValue("AlphacamBridge", false);
                }
                catch { }
            }
        }
        catch { }
    }
}

// ────────────────────────────────────────────────────────────────
//  No-op authenticator for development
// ────────────────────────────────────────────────────────────────

/// <summary>
/// No-op authenticator for development/testing. Allows all connections with all scopes.
/// </summary>
internal sealed class NoOpAuthenticator : IAuthenticator
{
    public Task<AuthResult> AuthenticateAsync(string credential)
    {
        return Task.FromResult(new AuthResult(
            true,
            "anonymous",
            new[] { AuthScope.Read, AuthScope.Write, AuthScope.Machining, AuthScope.FileIo, AuthScope.Admin }));
    }
}

// ────────────────────────────────────────────────────────────────
//  Simple file logger (no external NuGet dependency)
// ────────────────────────────────────────────────────────────────

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly object _lock = new();

    public FileLoggerProvider(string path) => _path = path;
    public ILogger CreateLogger(string categoryName) => new FileLogger(_path, categoryName, _lock);
    public void Dispose() { }
}

internal sealed class FileLogger : ILogger
{
    private readonly string _path;
    private readonly string _category;
    private readonly object _lock;

    public FileLogger(string path, string category, object lockObj)
    {
        _path = path;
        _category = category;
        _lock = lockObj;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_category}] {formatter(state, exception)}";
        if (exception != null)
            line += Environment.NewLine + exception.ToString();
        lock (_lock)
        {
            try { File.AppendAllText(_path, line + Environment.NewLine); }
            catch { }
        }
    }
}
