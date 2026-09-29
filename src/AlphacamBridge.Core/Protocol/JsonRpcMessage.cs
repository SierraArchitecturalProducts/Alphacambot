using System.Text.Json.Serialization;

namespace AlphacamBridge.Core.Protocol;

/// <summary>
/// Standard JSON-RPC 2.0 error codes.
/// </summary>
public static class JsonRpcErrorCode
{
    /// <summary>Invalid JSON was received by the server.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON sent is not a valid Request object.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>The method does not exist / is not available.</summary>
    public const int MethodNotFound = -32601;

    /// <summary>Invalid method parameter(s).</summary>
    public const int InvalidParams = -32602;

    /// <summary>Internal JSON-RPC error.</summary>
    public const int InternalError = -32603;

    /// <summary>Custom: Client is not authenticated.</summary>
    public const int Unauthorized = -32000;

    /// <summary>Custom: Client lacks required permissions for this operation.</summary>
    public const int Forbidden = -32001;

    /// <summary>Custom: Alphacam operation failed.</summary>
    public const int AlphacamError = -32002;

    /// <summary>Custom: Path is not allowed by security policy.</summary>
    public const int PathNotAllowed = -32003;
}

/// <summary>
/// Represents a JSON-RPC 2.0 error response.
/// </summary>
public record JsonRpcError(
    int Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Data = null);

/// <summary>
/// Represents a JSON-RPC 2.0 request.
/// </summary>
public record JsonRpcRequest(
    string Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Params = null,
    [property: JsonPropertyName("id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Id = null)
{
    /// <summary>
    /// Gets or sets the JSON-RPC version (always "2.0").
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
}

/// <summary>
/// Represents a JSON-RPC 2.0 response (successful result).
/// </summary>
public record JsonRpcResponse(
    [property: JsonPropertyName("id")]
    object? Id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Result = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonRpcError? Error = null)
{
    /// <summary>
    /// Gets or sets the JSON-RPC version (always "2.0").
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    /// <summary>
    /// Creates a successful response with a result.
    /// </summary>
    public static JsonRpcResponse Success(object? id, object? result) =>
        new(Id: id, Result: result, Error: null);

    /// <summary>
    /// Creates an error response.
    /// </summary>
    public static JsonRpcResponse Failure(object? id, JsonRpcError error) =>
        new(Id: id, Result: null, Error: error);

    /// <summary>
    /// Creates an error response with standard error details.
    /// </summary>
    public static JsonRpcResponse Failure(object? id, int code, string message, object? data = null) =>
        new(Id: id, Result: null, Error: new JsonRpcError(code, message, data));

    /// <summary>
    /// Creates a MethodNotFound error response.
    /// </summary>
    public static JsonRpcResponse MethodNotFound(object? id = null, string message = "Method not found") =>
        Failure(id, JsonRpcErrorCode.MethodNotFound, message);

    /// <summary>
    /// Creates an InvalidParams error response.
    /// </summary>
    public static JsonRpcResponse InvalidParams(object? id = null, string message = "Invalid parameters") =>
        Failure(id, JsonRpcErrorCode.InvalidParams, message);

    /// <summary>
    /// Creates an InternalError response.
    /// </summary>
    public static JsonRpcResponse InternalError(object? id = null, string message = "Internal server error") =>
        Failure(id, JsonRpcErrorCode.InternalError, message);
}

/// <summary>
/// Represents a JSON-RPC 2.0 notification (no Id field, used for unsolicited events).
/// </summary>
public record JsonRpcNotification(
    string Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Params = null)
{
    /// <summary>
    /// Gets or sets the JSON-RPC version (always "2.0").
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";
}
