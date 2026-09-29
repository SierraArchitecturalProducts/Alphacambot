using AlphaCAMMill;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Path = System.IO.Path;
using File = System.IO.File;

namespace CambotAlphacam.Models
{
    // ────────────────────────────────────────────────────────────────
    //  Alphacam .NET Add-In Entry Point
    //
    //  Alphacam scans DotNetAdd-Ins\ for .acamaddin manifests, loads
    //  the named assembly, and creates an instance of the AlphacamEvents
    //  class, passing the IAlphaCamApp COM object to the constructor.
    //
    //  This shim:
    //  1. Registers IAlphaCamApp in the COM Running Object Table (ROT)
    //     so the out-of-process Launcher can access it.
    //  2. Launches the .NET 8 bridge (AlphacamBridge.Launcher.exe)
    //     which hosts the named-pipe JSON-RPC server.
    // ────────────────────────────────────────────────────────────────

    public sealed class AlphacamEvents : IDisposable
    {
        // Well-known CLSID shared between the shim and the Launcher.
        // The Launcher calls GetActiveObject with this CLSID to obtain
        // a cross-process proxy to the IAlphaCamApp instance.
        private static readonly Guid AlphacamBridgeClsid =
            new Guid("F47AC10B-58CC-4372-A567-0E02B2C3D479");

        private const uint ACTIVEOBJECT_STRONG = 0;

        [DllImport("oleaut32.dll")]
        private static extern int RegisterActiveObject(
            [MarshalAs(UnmanagedType.IUnknown)] object punk,
            ref Guid rclsid,
            uint dwFlags,
            out int pdwRegister);

        [DllImport("oleaut32.dll")]
        private static extern int RevokeActiveObject(
            int dwRegister, IntPtr pvReserved);

        private bool _disposed;
        private int _rotHandle;
        private readonly string _logPath;
        private readonly IAlphaCamApp _alphacamApp;
        private AddInInterface _addInInterface;
        private ComCommandServer _commandServer;
        private System.Threading.Timer _launcherKeepalive;

        public AlphacamEvents(IAlphaCamApp app)
        {
            _alphacamApp = app;

            _logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlphacamBridge", "shim.log");
            try
            {
                var logDir = Path.GetDirectoryName(_logPath);
                if (!System.IO.Directory.Exists(logDir))
                    System.IO.Directory.CreateDirectory(logDir);
            }
            catch { }

            Log("AlphacamEvents constructor called");

            // Register IAlphaCamApp in the ROT so the Launcher can find it
            try
            {
                var clsid = AlphacamBridgeClsid;
                int hr = RegisterActiveObject(app, ref clsid, ACTIVEOBJECT_STRONG, out _rotHandle);
                if (hr >= 0)
                    Log(string.Format("Registered IAlphaCamApp in ROT (handle={0})", _rotHandle));
                else
                    Log(string.Format("Failed to register in ROT: 0x{0:X8}", hr));
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR registering in ROT: {0}", ex));
            }

            // Start the in-process command pipe server
            try
            {
                _commandServer = new ComCommandServer(app, _logPath);
                _commandServer.Start();
                Log("ComCommandServer started");
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR starting ComCommandServer: {0}", ex));
            }

            try
            {
                // Subscribe to InitAlphacamAddIn so Alphacam considers
                // this add-in properly loaded and doesn't dispose it.
                IFrame frame = app.Frame;
                _addInInterface = frame.CreateAddInInterface();
                _addInInterface.InitAlphacamAddIn += OnInitAddin;
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR subscribing to InitAlphacamAddIn: {0}", ex));
            }

            // Orphan-Acam.exe fix: when the user closes Alphacam, release
            // everything that would otherwise keep the process alive (the
            // ROT strong reference and the command pipe server). Without
            // this, Acam.exe lingers invisibly and steals bridge calls from
            // any Alphacam window the user opens next.
            //
            // BeforeExit fires when Alphacam itself is exiting; BeforeUnload
            // fires when the add-in is being unloaded (Utils > Add-Ins
            // uncheck, or exit). Both are parameterless events on
            // AddInInterface (confirmed against Interop.AlphaCAMMill.dll).
            try
            {
                _addInInterface.BeforeExit += OnBeforeExit;
                _addInInterface.BeforeUnload += OnBeforeUnload;
                Log("Subscribed to BeforeExit/BeforeUnload for orphan cleanup");
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR subscribing to BeforeExit/BeforeUnload (orphan cleanup unavailable): {0}", ex));
            }
        }

        private void OnBeforeExit()
        {
            Log("BeforeExit fired — releasing ROT registration and stopping command server");
            DisposeInternal();
        }

        private void OnBeforeUnload()
        {
            Log("BeforeUnload fired — releasing ROT registration and stopping command server");
            DisposeInternal();
        }

        private void OnInitAddin(AcamInitAddInAction Action, EventData Data)
        {
            Log("InitAlphacamAddIn fired");

            try
            {
                EnsureLauncherRunning();
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR starting Launcher: {0}", ex));
            }

            // Keep a Launcher alive for THIS Alphacam. If the previous
            // Alphacam's Launcher exits after we started (its shim-pipe
            // watchdog fires ~1 min after a close), this timer starts a
            // fresh one so the bridge heals without user intervention.
            try
            {
                _launcherKeepalive = new System.Threading.Timer(_ =>
                {
                    try { EnsureLauncherRunning(); } catch { }
                }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                Log(string.Format("ERROR starting launcher keepalive timer: {0}", ex));
            }

            Data.ReturnCode = 0;
        }

        // ────────────────────────────────────────────────────────────
        //  Launcher management
        // ────────────────────────────────────────────────────────────

        private void EnsureLauncherRunning()
        {
            // Check if already running
            try
            {
                var procs = Process.GetProcessesByName("AlphacamBridge.Launcher");
                if (procs.Length > 0)
                {
                    Log(string.Format("Launcher already running (PID {0})", procs[0].Id));
                    return;
                }
            }
            catch { }

            var launcherPath = FindLauncher();
            if (launcherPath == null)
            {
                Log("FATAL: Could not find AlphacamBridge.Launcher.exe");
                return;
            }

            Log(string.Format("Starting Launcher: {0}", launcherPath));

            var startInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(launcherPath),
            };

            var proc = Process.Start(startInfo);

            if (proc == null || proc.HasExited)
            {
                Log("FATAL: Launcher process failed to start");
                return;
            }

            Log(string.Format("Launcher started (PID {0})", proc.Id));
            proc.Dispose();
        }

        private string FindLauncher()
        {
            var shimDir = GetShimDirectory();

            // 1. Same directory as shim (installed layout)
            var path = Path.Combine(shimDir, "AlphacamBridge.Launcher.exe");
            if (File.Exists(path)) return path;

            // 2. Standard install location
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "AlphacamBridge", "AlphacamBridge.Launcher.exe");
            if (File.Exists(appDataPath)) return appDataPath;

            // 3. Development layout — relative to source tree
            var devPaths = new[]
            {
                Path.GetFullPath(Path.Combine(shimDir,
                    @"..\..\..\..\AlphacamBridge.Launcher\bin\Debug\net8.0-windows\AlphacamBridge.Launcher.exe")),
                Path.GetFullPath(Path.Combine(shimDir,
                    @"..\..\..\..\AlphacamBridge.Launcher\bin\Release\net8.0-windows\AlphacamBridge.Launcher.exe")),
            };

            foreach (var devPath in devPaths)
            {
                if (File.Exists(devPath)) return devPath;
            }

            return null;
        }

        // ────────────────────────────────────────────────────────────
        //  Cleanup
        // ────────────────────────────────────────────────────────────

        public void Dispose() => DisposeInternal();
        ~AlphacamEvents() => DisposeInternal();

        private void DisposeInternal()
        {
            if (_disposed) return;
            Log("Dispose called");

            // Stop the launcher keepalive so a closing Alphacam doesn't
            // respawn a Launcher on its way out.
            try { _launcherKeepalive?.Dispose(); } catch { }
            _launcherKeepalive = null;

            // Stop command server
            try { _commandServer?.Dispose(); } catch { }
            _commandServer = null;

            // Revoke ROT registration
            if (_rotHandle != 0)
            {
                try
                {
                    RevokeActiveObject(_rotHandle, IntPtr.Zero);
                    Log("Revoked ROT registration");
                }
                catch { }
                _rotHandle = 0;
            }

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        // ────────────────────────────────────────────────────────────
        //  Helpers
        // ────────────────────────────────────────────────────────────

        private static string GetShimDirectory()
        {
            return Path.GetDirectoryName(typeof(AlphacamEvents).Assembly.Location)
                   ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        private void Log(string message)
        {
            if (_logPath == null) return;
            try
            {
                File.AppendAllText(_logPath,
                    string.Format("[{0:O}] {1}\r\n", DateTime.Now, message));
            }
            catch { }
        }
    }
}
