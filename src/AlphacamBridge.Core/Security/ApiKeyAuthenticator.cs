using System.Security.Cryptography;
using System.Text;

namespace AlphacamBridge.Core.Security;

/// <summary>
/// Represents the result of an authentication attempt.
/// </summary>
public record AuthResult(bool IsAuthenticated, string? ClientId = null, string[] AllowedScopes = null!)
{
    /// <summary>
    /// Allowed scopes for the authenticated client, or empty if not authenticated.
    /// </summary>
    public string[] AllowedScopes { get; init; } = AllowedScopes ?? Array.Empty<string>();
}

/// <summary>
/// Defines scope constants for API authorization.
/// </summary>
public static class AuthScope
{
    /// <summary>Read-only access to geometry and tool data.</summary>
    public const string Read = "read";

    /// <summary>Write access to modify geometry and parameters.</summary>
    public const string Write = "write";

    /// <summary>Access to CNC machining operations.</summary>
    public const string Machining = "machining";

    /// <summary>Access to file I/O operations.</summary>
    public const string FileIo = "file_io";

    /// <summary>Administrative operations like shutdown and config changes.</summary>
    public const string Admin = "admin";

    /// <summary>All standard scopes.</summary>
    public static readonly string[] AllScopes = { Read, Write, Machining, FileIo, Admin };
}

/// <summary>
/// Interface for implementing authentication strategies.
/// </summary>
public interface IAuthenticator
{
    /// <summary>
    /// Authenticates a credential and returns the result with scopes.
    /// </summary>
    Task<AuthResult> AuthenticateAsync(string credential);
}

/// <summary>
/// Implements API key-based authentication using SHA-256 hashes.
/// Keys are pre-hashed and stored in configuration for security.
/// </summary>
public class ApiKeyAuthenticator : IAuthenticator
{
    private readonly Dictionary<string, ApiKeyEntry> _keyStore;

    /// <summary>
    /// Configuration entry for an API key.
    /// </summary>
    public record ApiKeyEntry(string ClientId, string KeyHash, string[] AllowedScopes);

    /// <summary>
    /// Initializes a new authenticator with a set of known API keys.
    /// </summary>
    /// <param name="apiKeys">Dictionary mapping clientId to (hash, scopes) tuples.</param>
    public ApiKeyAuthenticator(Dictionary<string, (string keyHash, string[] scopes)> apiKeys)
    {
        _keyStore = apiKeys.ToDictionary(
            kvp => kvp.Key,
            kvp => new ApiKeyEntry(kvp.Key, kvp.Value.keyHash, kvp.Value.scopes));
    }

    /// <summary>
    /// Authenticates using a plaintext API key by comparing against stored SHA-256 hashes.
    /// </summary>
    public Task<AuthResult> AuthenticateAsync(string credential)
    {
        ArgumentException.ThrowIfNullOrEmpty(credential);

        var credentialHash = HashApiKey(credential);

        foreach (var entry in _keyStore.Values)
        {
            if (entry.KeyHash == credentialHash)
            {
                return Task.FromResult(new AuthResult(
                    IsAuthenticated: true,
                    ClientId: entry.ClientId,
                    AllowedScopes: entry.AllowedScopes));
            }
        }

        return Task.FromResult(new AuthResult(IsAuthenticated: false));
    }

    /// <summary>
    /// Generates a SHA-256 hash of a plaintext API key for secure storage.
    /// Use this when generating configuration files.
    /// </summary>
    public static string HashApiKey(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(plaintext));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
