using System.IO.Pipes;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AlphacamBridge.Core.Security;

namespace AlphacamBridge.Core.Protocol;

/// <summary>
/// Delegate for handling incoming JSON-RPC requests.
/// </summary>
public delegate Task<JsonRpcResponse> RequestHandler(JsonRpcRequest request, string clientId, string[] allowedScopes);

/// <summary>
/// Hosts a named pipe server that handles JSON-RPC protocol with authentication.
/// Handles one client connection at a time (Alphacam is single-threaded).
/// </summary>
public class PipeServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly PipeSecurity _pipeSecurity;
    private readonly IAuthenticator _authenticator;
    private readonly ILogger _logger;
    private readonly int _maxMessageSize;
    private readonly int _authTimeoutSeconds;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    /// <summary>
    /// Raised when a request is received from the connected client.
    /// Handler should process the request and return a response.
    /// </summary>
    public event RequestHandler? OnRequestReceived;

    /// <summary>
    /// Initializes a new pipe server.
    /// </summary>
    /// <param name="pipeName">Name of the named pipe (without \\.\pipe\ prefix).</param>
    /// <param name="pipeSecurity">Security settings for pipe access control.</param>
    /// <param name="authenticator">Authenticator for validating client credentials.</param>
    /// <param name="maxMessageSize">Maximum allowed message size in bytes (default 10MB).</param>
    /// <param name="authTimeoutSeconds">Timeout for authentication handshake in seconds (default 30).</param>
    /// <param name="logger">Logger instance (optional).</param>
    public PipeServer(
        string pipeName,
        PipeSecurity pipeSecurity,
        IAuthenticator authenticator,
        int maxMessageSize = 10 * 1024 * 1024,
        int authTimeoutSeconds = 30,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(pipeSecurity);
        ArgumentNullException.ThrowIfNull(authenticator);

        if (maxMessageSize <= 0)
            throw new ArgumentException("Max message size must be positive", nameof(maxMessageSize));
        if (authTimeoutSeconds <= 0)
            throw new ArgumentException("Auth timeout must be positive", nameof(authTimeoutSeconds));

        _pipeName = pipeName;
        _pipeSecurity = pipeSecurity;
        _authenticator = authenticator;
        _maxMessageSize = maxMessageSize;
        _authTimeoutSeconds = authTimeoutSeconds;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Starts the pipe server and listens for incoming connections.
    /// Blocks until cancellation is requested.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _serverTask = ListenLoop(_serverCts.Token);
        await _serverTask;
    }

    /// <summary>
    /// Sends an unsolicited notification to the currently connected client.
    /// Only works if a client is actively connected.
    /// </summary>
    public async Task SendNotificationAsync(JsonRpcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // This would be implemented when we have an active connection reference
        // For now, this is a placeholder for future implementation
        await Task.CompletedTask;
    }

    /// <summary>
    /// Main server loop that listens for and handles client connections.
    /// </summary>
    private async Task ListenLoop(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Pipe server starting on pipe '{PipeName}'", _pipeName);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream? pipeStream = null;

                try
                {
                    pipeStream = NamedPipeServerStreamAcl.Create(
                        _pipeName,
                        PipeDirection.InOut,
                        1, // Single client at a time
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        inBufferSize: 65536,
                        outBufferSize: 65536,
                        pipeSecurity: _pipeSecurity);

                    _logger.LogInformation("Waiting for client connection...");

                    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                        connectCts.Token, cancellationToken);

                    await pipeStream.WaitForConnectionAsync(linkedCts.Token);

                    _logger.LogInformation("Client connected");

                    // Handle this client connection
                    await HandleClientAsync(pipeStream, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Connection wait cancelled");
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "I/O error while listening for connections");
                }
                finally
                {
                    pipeStream?.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipe server error");
            throw;
        }
        finally
        {
            _logger.LogInformation("Pipe server stopped");
        }
    }

    /// <summary>
    /// Handles a single client connection through auth and request/response loop.
    /// </summary>
    private async Task HandleClientAsync(NamedPipeServerStream pipeStream, CancellationToken cancellationToken)
    {
        string clientId = "unknown";

        try
        {
            var buffer = new byte[_maxMessageSize];

            // Authenticate the client
            var (isAuthenticated, id, scopes) = await AuthenticateClientAsync(pipeStream, buffer, cancellationToken);

            if (!isAuthenticated)
            {
                _logger.LogWarning("Client authentication failed");
                return;
            }

            clientId = id ?? "unknown";
            _logger.LogInformation("Client '{ClientId}' authenticated with scopes: {Scopes}", clientId, string.Join(", ", scopes));

            // Handle requests from the authenticated client
            await HandleRequestsAsync(pipeStream, buffer, clientId, scopes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Client operation cancelled");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Client I/O error");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error handling client '{ClientId}'", clientId);
        }
    }

    /// <summary>
    /// Authenticates the client by reading and validating an auth message.
    /// </summary>
    private async Task<(bool IsAuthenticated, string? ClientId, string[] Scopes)> AuthenticateClientAsync(
        NamedPipeServerStream pipeStream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(_authTimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(authCts.Token, cancellationToken);

            var bytesRead = await pipeStream.ReadAsync(buffer, 0, buffer.Length, linkedCts.Token);

            if (bytesRead == 0)
            {
                _logger.LogWarning("Client disconnected during auth");
                return (false, null, Array.Empty<string>());
            }

            // Parse auth message (simple JSON with "key" field)
            var authData = JsonSerializer.Deserialize<JsonElement>(
                new ReadOnlySpan<byte>(buffer, 0, bytesRead));

            if (!authData.TryGetProperty("key", out var keyElement))
            {
                _logger.LogWarning("Auth message missing 'key' field");
                return (false, null, Array.Empty<string>());
            }

            var key = keyElement.GetString();
            if (string.IsNullOrEmpty(key))
            {
                _logger.LogWarning("Auth message 'key' field is empty");
                return (false, null, Array.Empty<string>());
            }

            var result = await _authenticator.AuthenticateAsync(key);

            if (result.IsAuthenticated)
            {
                return (true, result.ClientId, result.AllowedScopes);
            }

            _logger.LogWarning("Authentication failed for client");
            return (false, null, Array.Empty<string>());
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Authentication timeout");
            return (false, null, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Handles the request/response loop with an authenticated client.
    /// </summary>
    private async Task HandleRequestsAsync(
        NamedPipeServerStream pipeStream,
        byte[] buffer,
        string clientId,
        string[] allowedScopes,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && pipeStream.IsConnected)
        {
            try
            {
                // Read message with length prefix
                var bytesRead = await pipeStream.ReadAsync(buffer, 0, 4, cancellationToken);

                if (bytesRead < 4)
                {
                    _logger.LogDebug("Client disconnected");
                    break;
                }

                // Check if we have a complete message
                var messageBuffer = new Memory<byte>(buffer);
                if (!JsonRpcSerializer.TryReadFramedMessage(buffer, out var messageSize))
                {
                    _logger.LogWarning("Message size exceeds buffer");
                    continue;
                }

                // Read the rest of the message
                if (messageSize > 4)
                {
                    bytesRead += await pipeStream.ReadAsync(buffer, 4, messageSize - 4, cancellationToken);
                }

                if (bytesRead < messageSize)
                {
                    _logger.LogDebug("Client disconnected mid-message");
                    break;
                }

                // Parse and handle the request
                try
                {
                    var request = JsonRpcSerializer.DeserializeRequest(new ReadOnlySpan<byte>(buffer, 0, messageSize));
                    var response = await HandleRequestAsync(request, clientId, allowedScopes, cancellationToken);

                    // Send response
                    var responseBytes = JsonRpcSerializer.Serialize(response);
                    await pipeStream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
                    await pipeStream.FlushAsync(cancellationToken);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "JSON parsing error");

                    var errorResponse = JsonRpcResponse.Failure(
                        null,
                        JsonRpcErrorCode.ParseError,
                        "Invalid JSON");

                    var responseBytes = JsonRpcSerializer.Serialize(errorResponse);
                    await pipeStream.WriteAsync(responseBytes, 0, responseBytes.Length, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                _logger.LogDebug("I/O error during message handling");
                break;
            }
        }
    }

    /// <summary>
    /// Processes a single JSON-RPC request.
    /// </summary>
    private async Task<JsonRpcResponse> HandleRequestAsync(
        JsonRpcRequest request,
        string clientId,
        string[] allowedScopes,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = OnRequestReceived;

            if (handler == null)
            {
                return JsonRpcResponse.Failure(
                    request.Id,
                    JsonRpcErrorCode.InternalError,
                    "No request handler registered");
            }

            var response = await handler(request, clientId, allowedScopes);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling request");

            return JsonRpcResponse.Failure(
                request.Id,
                JsonRpcErrorCode.InternalError,
                "Internal server error");
        }
    }

    /// <summary>
    /// Stops the pipe server and cleans up resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _serverCts?.Cancel();

        if (_serverTask != null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _serverCts?.Dispose();
    }
}
