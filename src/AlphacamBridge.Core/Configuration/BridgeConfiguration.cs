using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlphacamBridge.Core.Security;

namespace AlphacamBridge.Core.Configuration;

/// <summary>
/// Configuration for an API key with scopes.
/// </summary>
public record ApiKeyConfig(
    string KeyHash,
    [property: JsonPropertyName("scopes")]
    string[] Scopes = null!)
{
    /// <summary>
    /// Allowed scopes for this key, or empty if unrestricted.
    /// </summary>
    public string[] Scopes { get; init; } = Scopes ?? Array.Empty<string>();
}

/// <summary>
/// Path-based access control configuration.
/// </summary>
public record PathAccessConfig(
    [property: JsonPropertyName("read")]
    List<string> Read = null!,
    [property: JsonPropertyName("write")]
    List<string> Write = null!,
    [property: JsonPropertyName("denied")]
    List<string> Denied = null!)
{
    /// <summary>Paths allowed for read operations.</summary>
    public List<string> Read { get; init; } = Read ?? new();

    /// <summary>Paths allowed for write operations.</summary>
    public List<string> Write { get; init; } = Write ?? new();

    /// <summary>Paths denied for all operations.</summary>
    public List<string> Denied { get; init; } = Denied ?? new();
}

/// <summary>
/// Strongly-typed configuration for the Alphacam bridge.
/// Loaded from JSON file on startup.
/// </summary>
public class BridgeConfiguration
{
    /// <summary>Name of the named pipe.</summary>
    [JsonPropertyName("pipeName")]
    public string PipeName { get; set; } = "AlphacamBridge";

    /// <summary>Whether to require authentication for pipe connections.</summary>
    [JsonPropertyName("authenticationEnabled")]
    public bool AuthenticationEnabled { get; set; } = true;

    /// <summary>API keys with their hashes and allowed scopes.</summary>
    [JsonPropertyName("apiKeys")]
    public Dictionary<string, ApiKeyConfig> ApiKeys { get; set; } = new();

    /// <summary>Path-based access control configuration.</summary>
    [JsonPropertyName("allowedPaths")]
    public PathAccessConfig AllowedPaths { get; set; } = new();

    /// <summary>Maximum allowed message size in bytes.</summary>
    [JsonPropertyName("maxMessageSizeBytes")]
    public int MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>Timeout for authentication handshake in seconds.</summary>
    [JsonPropertyName("authTimeoutSeconds")]
    public int AuthTimeoutSeconds { get; set; } = 30;

    /// <summary>Logging level (Debug, Information, Warning, Error, Critical).</summary>
    [JsonPropertyName("logLevel")]
    public string LogLevel { get; set; } = "Information";

    /// <summary>Additional Windows SIDs allowed to access the pipe.</summary>
    [JsonPropertyName("allowedPipeSids")]
    public List<string> AllowedPipeSids { get; set; } = new();

    /// <summary>
    /// Loads configuration from a JSON file.
    /// </summary>
    /// <param name="path">Path to the configuration JSON file.</param>
    /// <returns>Loaded configuration, or defaults if file not found.</returns>
    public static BridgeConfiguration LoadFromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Configuration file not found: {path}", path);
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            };

            var config = JsonSerializer.Deserialize<BridgeConfiguration>(json, options)
                ?? new BridgeConfiguration();

            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse configuration file: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read configuration file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Saves the current configuration to a JSON file.
    /// </summary>
    public void SaveToFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(path, json);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to write configuration file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a SHA-256 hash of a plaintext API key.
    /// Use this when creating configuration files.
    /// </summary>
    public static string GenerateApiKeyHash(string plaintext)
    {
        return ApiKeyAuthenticator.HashApiKey(plaintext);
    }

    /// <summary>
    /// Validates the configuration for consistency and required values.
    /// </summary>
    /// <returns>List of validation errors, empty if valid.</returns>
    public List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(PipeName))
        {
            errors.Add("PipeName is required");
        }

        if (MaxMessageSizeBytes <= 0)
        {
            errors.Add("MaxMessageSizeBytes must be greater than 0");
        }

        if (AuthTimeoutSeconds <= 0)
        {
            errors.Add("AuthTimeoutSeconds must be greater than 0");
        }

        if (AuthenticationEnabled && ApiKeys.Count == 0)
        {
            errors.Add("AuthenticationEnabled is true but no API keys are configured");
        }

        // Validate API key formats
        foreach (var (clientId, keyConfig) in ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(keyConfig.KeyHash))
            {
                errors.Add($"API key for client '{clientId}' has empty KeyHash");
            }

            if (keyConfig.KeyHash.Length != 64) // SHA-256 hex = 64 chars
            {
                errors.Add($"API key for client '{clientId}' has invalid KeyHash length (expected 64, got {keyConfig.KeyHash.Length})");
            }
        }

        // Validate SIDs
        foreach (var sid in AllowedPipeSids)
        {
            if (string.IsNullOrWhiteSpace(sid))
            {
                errors.Add("Empty SID in AllowedPipeSids");
            }
        }

        return errors;
    }

    /// <summary>
    /// Creates a sample configuration with defaults and example API keys.
    /// </summary>
    public static BridgeConfiguration CreateSample()
    {
        return new BridgeConfiguration
        {
            PipeName = "AlphacamBridge",
            AuthenticationEnabled = true,
            ApiKeys = new Dictionary<string, ApiKeyConfig>
            {
                {
                    "example-client",
                    new ApiKeyConfig(
                        KeyHash: GenerateApiKeyHash("change-me-in-production"),
                        Scopes: new[] { AuthScope.Read, AuthScope.Write })
                }
            },
            AllowedPaths = new PathAccessConfig(
                Read: new List<string>
                {
                    Environment.GetEnvironmentVariable("LICOMDIR") ?? @"C:\Alphacam",
                    Environment.GetEnvironmentVariable("LICOMDAT") ?? @"C:\Alphacam\Data"
                },
                Write: new List<string>
                {
                    Environment.ExpandEnvironmentVariables(@"%TEMP%\Alphacam")
                },
                Denied: new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System)
                }),
            MaxMessageSizeBytes = 10 * 1024 * 1024,
            AuthTimeoutSeconds = 30,
            LogLevel = "Information",
            AllowedPipeSids = new List<string>()
        };
    }
}
