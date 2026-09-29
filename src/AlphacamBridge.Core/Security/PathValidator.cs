namespace AlphacamBridge.Core.Security;

/// <summary>
/// Represents the type of file operation being performed.
/// </summary>
public enum PathOperation
{
    /// <summary>Read access to a file or directory.</summary>
    Read,

    /// <summary>Write or modify a file or directory.</summary>
    Write,

    /// <summary>Delete a file or directory.</summary>
    Delete
}

/// <summary>
/// Interface for validating file paths against security policies.
/// </summary>
public interface IPathValidator
{
    /// <summary>
    /// Determines whether the specified path is allowed for the given operation.
    /// </summary>
    /// <param name="path">The file path to validate.</param>
    /// <param name="operation">The type of operation being performed.</param>
    /// <returns>True if the path is allowed; false otherwise.</returns>
    bool IsPathAllowed(string path, PathOperation operation);

    /// <summary>Validates a path for drawing file operations (read or write).</summary>
    bool IsValidDrawingPath(string path);

    /// <summary>Validates a path for tool file operations (read or write).</summary>
    bool IsValidToolPath(string path);

    /// <summary>Validates a path for import file operations (read only).</summary>
    bool IsValidImportPath(string path);
}

/// <summary>
/// Validates file paths against configurable allowlists and denylists.
/// Prevents directory traversal attacks and enforces path-based access control.
/// </summary>
public class PathValidator : IPathValidator
{
    private readonly Dictionary<PathOperation, HashSet<string>> _allowedPaths;
    private readonly Dictionary<PathOperation, HashSet<string>> _deniedPaths;

    /// <summary>
    /// Initializes a new path validator with default rules.
    /// Default allows reads from common Alphacam directories and denies writes to system paths.
    /// </summary>
    public PathValidator()
    {
        _allowedPaths = new Dictionary<PathOperation, HashSet<string>>();
        _deniedPaths = new Dictionary<PathOperation, HashSet<string>>();

        // Initialize with default rules
        InitializeDefaults();
    }

    /// <summary>
    /// Initializes a path validator with custom allow and deny lists.
    /// </summary>
    /// <param name="allowedPathsByOperation">Allowed paths per operation.</param>
    /// <param name="deniedPathsByOperation">Denied paths per operation.</param>
    public PathValidator(
        Dictionary<PathOperation, List<string>> allowedPathsByOperation,
        Dictionary<PathOperation, List<string>> deniedPathsByOperation)
    {
        _allowedPaths = allowedPathsByOperation.ToDictionary(
            kvp => kvp.Key,
            kvp => new HashSet<string>(kvp.Value.Select(NormalizePath), StringComparer.OrdinalIgnoreCase));

        _deniedPaths = deniedPathsByOperation.ToDictionary(
            kvp => kvp.Key,
            kvp => new HashSet<string>(kvp.Value.Select(NormalizePath), StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Determines whether the specified path is allowed for the given operation.
    /// Paths are normalized to prevent traversal attacks.
    /// </summary>
    public bool IsPathAllowed(string path, PathOperation operation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var normalizedPath = NormalizePath(path);

        // Check deny list first (most restrictive)
        if (_deniedPaths.TryGetValue(operation, out var deniedSet) && IsPathInSet(normalizedPath, deniedSet))
        {
            return false;
        }

        // Check allow list
        if (_allowedPaths.TryGetValue(operation, out var allowedSet) && IsPathInSet(normalizedPath, allowedSet))
        {
            return true;
        }

        // Default deny if no allow rule matches
        return false;
    }

    /// <summary>
    /// Adds an allowed path for a specific operation.
    /// </summary>
    public void AddAllowedPath(PathOperation operation, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!_allowedPaths.ContainsKey(operation))
        {
            _allowedPaths[operation] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        _allowedPaths[operation].Add(NormalizePath(path));
    }

    /// <summary>
    /// Adds a denied path for a specific operation.
    /// </summary>
    public void AddDeniedPath(PathOperation operation, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!_deniedPaths.ContainsKey(operation))
        {
            _deniedPaths[operation] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        _deniedPaths[operation].Add(NormalizePath(path));
    }

    /// <summary>
    /// Clears all custom rules and resets to defaults.
    /// </summary>
    public void ResetToDefaults()
    {
        _allowedPaths.Clear();
        _deniedPaths.Clear();
        InitializeDefaults();
    }

    /// <summary>
    /// Normalizes a path to prevent traversal attacks.
    /// Resolves relative segments and converts to absolute form.
    /// </summary>
    private static string NormalizePath(string path)
    {
        // Get full path to prevent .. traversal
        var fullPath = Path.GetFullPath(path);

        // Ensure consistent separators
        fullPath = fullPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // Ensure trailing separator for directory matching
        if (!fullPath.EndsWith(Path.DirectorySeparatorChar))
        {
            fullPath += Path.DirectorySeparatorChar;
        }

        return fullPath;
    }

    /// <summary>
    /// Checks if a path matches or is under any path in the given set.
    /// </summary>
    private static bool IsPathInSet(string normalizedPath, HashSet<string> pathSet)
    {
        foreach (var allowedPath in pathSet)
        {
            // Exact match or path is under the allowed directory
            if (normalizedPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Initializes default path rules for typical Alphacam usage.
    /// </summary>
    private void InitializeDefaults()
    {
        // Allow reads from common Alphacam directories
        var readAllowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(GetAlphacamDirectory("licomdir")),
            NormalizePath(GetAlphacamDirectory("licomdat")),
        };

        _allowedPaths[PathOperation.Read] = readAllowedPaths;

        // Deny writes to system and protected directories
        var writeDeniedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
            NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.System)),
            NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
        };

        _deniedPaths[PathOperation.Write] = writeDeniedPaths;
        _deniedPaths[PathOperation.Delete] = new HashSet<string>(writeDeniedPaths, StringComparer.OrdinalIgnoreCase);

        // No explicit allow for writes - requires explicit configuration
        if (!_allowedPaths.ContainsKey(PathOperation.Write))
        {
            _allowedPaths[PathOperation.Write] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        if (!_allowedPaths.ContainsKey(PathOperation.Delete))
        {
            _allowedPaths[PathOperation.Delete] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets the path to an Alphacam directory by key.
    /// Falls back to common default locations if environment variable not set.
    /// </summary>
    private static string GetAlphacamDirectory(string key) => key switch
    {
        "licomdir" => Environment.GetEnvironmentVariable("LICOMDIR")
            ?? @"C:\Alphacam",
        "licomdat" => Environment.GetEnvironmentVariable("LICOMDAT")
            ?? @"C:\Alphacam\Data",
        _ => throw new ArgumentException($"Unknown Alphacam directory key: {key}", nameof(key))
    };

    /// <summary>
    /// Validates that a path is allowed for drawing file operations.
    /// </summary>
    public bool IsValidDrawingPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return IsPathAllowed(path, PathOperation.Read) || IsPathAllowed(path, PathOperation.Write);
    }

    /// <summary>
    /// Validates that a path is allowed for tool file operations.
    /// </summary>
    public bool IsValidToolPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return IsPathAllowed(path, PathOperation.Read) || IsPathAllowed(path, PathOperation.Write);
    }

    /// <summary>
    /// Validates that a path is allowed for import file operations.
    /// </summary>
    public bool IsValidImportPath(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return IsPathAllowed(path, PathOperation.Read);
    }
}
