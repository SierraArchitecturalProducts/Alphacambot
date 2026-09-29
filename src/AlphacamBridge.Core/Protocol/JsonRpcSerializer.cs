using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlphacamBridge.Core.Protocol;

/// <summary>
/// Handles serialization and deserialization of JSON-RPC messages.
/// Uses System.Text.Json with camelCase naming convention.
/// Implements length-prefixed framing for named pipe transport.
/// </summary>
public class JsonRpcSerializer
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Serializes a JSON-RPC response to a byte array with length prefix.
    /// Length prefix is 4-byte big-endian integer representing the JSON payload size.
    /// </summary>
    /// <param name="response">The response to serialize.</param>
    /// <returns>Serialized message with length prefix.</returns>
    public static byte[] Serialize(JsonRpcResponse response)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(response, DefaultOptions);
        return WrapWithLengthPrefix(json);
    }

    /// <summary>
    /// Serializes a JSON-RPC notification to a byte array with length prefix.
    /// </summary>
    /// <param name="notification">The notification to serialize.</param>
    /// <returns>Serialized message with length prefix.</returns>
    public static byte[] Serialize(JsonRpcNotification notification)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(notification, DefaultOptions);
        return WrapWithLengthPrefix(json);
    }

    /// <summary>
    /// Deserializes a JSON-RPC request from a byte array.
    /// Expects a 4-byte big-endian length prefix followed by the JSON payload.
    /// </summary>
    /// <param name="data">The serialized message with length prefix.</param>
    /// <returns>The deserialized request.</returns>
    /// <exception cref="JsonException">If the data is invalid or malformed.</exception>
    public static JsonRpcRequest DeserializeRequest(ReadOnlySpan<byte> data)
    {
        var json = UnwrapLengthPrefix(data, out var payloadSize);

        try
        {
            var request = JsonSerializer.Deserialize<JsonRpcRequest>(json, DefaultOptions)
                ?? throw new JsonException("Deserialized request was null");

            return request;
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Failed to deserialize JSON-RPC request: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Deserializes a JSON-RPC response from a byte array.
    /// Expects a 4-byte big-endian length prefix followed by the JSON payload.
    /// </summary>
    /// <param name="data">The serialized message with length prefix.</param>
    /// <returns>The deserialized response.</returns>
    /// <exception cref="JsonException">If the data is invalid or malformed.</exception>
    public static JsonRpcResponse DeserializeResponse(ReadOnlySpan<byte> data)
    {
        var json = UnwrapLengthPrefix(data, out var payloadSize);

        try
        {
            var response = JsonSerializer.Deserialize<JsonRpcResponse>(json, DefaultOptions)
                ?? throw new JsonException("Deserialized response was null");

            return response;
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Failed to deserialize JSON-RPC response: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Deserializes a JSON-RPC notification from a byte array.
    /// </summary>
    /// <param name="data">The serialized message with length prefix.</param>
    /// <returns>The deserialized notification.</returns>
    /// <exception cref="JsonException">If the data is invalid or malformed.</exception>
    public static JsonRpcNotification DeserializeNotification(ReadOnlySpan<byte> data)
    {
        var json = UnwrapLengthPrefix(data, out var payloadSize);

        try
        {
            var notification = JsonSerializer.Deserialize<JsonRpcNotification>(json, DefaultOptions)
                ?? throw new JsonException("Deserialized notification was null");

            return notification;
        }
        catch (JsonException ex)
        {
            throw new JsonException($"Failed to deserialize JSON-RPC notification: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Tries to read a complete framed message from a byte array.
    /// Returns true and sets the message span if a complete message is available.
    /// </summary>
    /// <param name="buffer">The buffer to read from.</param>
    /// <param name="messageSize">Output: the size of the complete message (including 4-byte prefix).</param>
    /// <returns>True if a complete message is available, false if more data is needed.</returns>
    public static bool TryReadFramedMessage(ReadOnlySpan<byte> buffer, out int messageSize)
    {
        messageSize = 0;

        if (buffer.Length < 4)
        {
            return false; // Need at least 4 bytes for length prefix
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(buffer);
        var totalSize = 4 + payloadLength;

        if (buffer.Length < totalSize)
        {
            return false; // Need more data
        }

        messageSize = totalSize;
        return true;
    }

    /// <summary>
    /// Adds a 4-byte big-endian length prefix to a JSON payload.
    /// </summary>
    private static byte[] WrapWithLengthPrefix(byte[] json)
    {
        var result = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(result, json.Length);
        Array.Copy(json, 0, result, 4, json.Length);
        return result;
    }

    /// <summary>
    /// Removes and validates a 4-byte big-endian length prefix from a buffer.
    /// </summary>
    private static ReadOnlySpan<byte> UnwrapLengthPrefix(ReadOnlySpan<byte> data, out int payloadSize)
    {
        if (data.Length < 4)
        {
            throw new JsonException("Message is too short to contain length prefix");
        }

        payloadSize = BinaryPrimitives.ReadInt32BigEndian(data);

        if (payloadSize < 0)
        {
            throw new JsonException("Invalid length prefix: negative payload size");
        }

        if (data.Length < 4 + payloadSize)
        {
            throw new JsonException("Message is shorter than declared by length prefix");
        }

        return data.Slice(4, payloadSize);
    }
}
