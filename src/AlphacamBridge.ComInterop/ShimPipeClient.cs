using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlphacamBridge.ComInterop;

/// <summary>
/// Named-pipe client that connects to the shim's command pipe (AlphacamBridgeCmd)
/// running inside Alphacam's process. Forwards JSON-RPC requests for in-process
/// COM execution, avoiding cross-process COM limitations.
/// </summary>
public class ShimPipeClient : IDisposable
{
    private const string PipeName = "AlphacamBridgeCmd";
    private const int ConnectTimeoutMs = 10_000;

    private readonly ILogger<ShimPipeClient> _logger;
    private NamedPipeClientStream? _pipe;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _disposed;

    public bool IsConnected => _pipe?.IsConnected ?? false;

    public ShimPipeClient(ILogger<ShimPipeClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Connect to the shim's command pipe.
    /// </summary>
    public void Connect()
    {
        if (_pipe?.IsConnected == true) return;

        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);

        _logger.LogInformation("Connecting to shim command pipe: {PipeName}", PipeName);
        _pipe.Connect(ConnectTimeoutMs);
        _logger.LogInformation("Connected to shim command pipe");
    }

    /// <summary>
    /// Send a JSON-RPC request to the shim and return the parsed response.
    /// </summary>
    public async Task<JsonElement> SendAsync(object? id, string method, object? parameters)
    {
        await _sendLock.WaitAsync();
        try
        {
            EnsureConnected();

            // Build the JSON-RPC request
            var request = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters ?? new object()
            };

            var requestJson = JsonSerializer.Serialize(request);
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);

            _logger.LogDebug("Sending to shim: {Method}", method);

            // Write length-prefixed message
            var lenBuf = new byte[4];
            lenBuf[0] = (byte)(requestBytes.Length >> 24);
            lenBuf[1] = (byte)(requestBytes.Length >> 16);
            lenBuf[2] = (byte)(requestBytes.Length >> 8);
            lenBuf[3] = (byte)(requestBytes.Length);

            await _pipe!.WriteAsync(lenBuf, 0, 4);
            await _pipe.WriteAsync(requestBytes, 0, requestBytes.Length);
            await _pipe.FlushAsync();

            // Read response length
            var respLenBuf = new byte[4];
            var bytesRead = await ReadExactAsync(_pipe, respLenBuf, 4);
            if (bytesRead < 4)
                throw new IOException("Shim pipe disconnected while reading response length");

            var respLen = (respLenBuf[0] << 24) | (respLenBuf[1] << 16) |
                          (respLenBuf[2] << 8) | respLenBuf[3];

            if (respLen <= 0 || respLen > 10 * 1024 * 1024)
                throw new IOException($"Invalid response length from shim: {respLen}");

            // Read response body
            var respBuf = new byte[respLen];
            bytesRead = await ReadExactAsync(_pipe, respBuf, respLen);
            if (bytesRead < respLen)
                throw new IOException("Shim pipe disconnected while reading response body");

            var responseJson = Encoding.UTF8.GetString(respBuf);
            _logger.LogDebug("Shim response: {Response}",
                responseJson.Length > 200 ? responseJson[..200] + "..." : responseJson);

            // Parse the JSON-RPC response
            var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.Clone();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Shim pipe connection lost, will reconnect on next call");
            _pipe?.Dispose();
            _pipe = null;
            throw;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EnsureConnected()
    {
        if (_pipe?.IsConnected != true)
        {
            _logger.LogInformation("Shim pipe not connected, reconnecting...");
            Connect();
        }
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer, total, count - total);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipe?.Dispose();
        _sendLock.Dispose();
    }
}
