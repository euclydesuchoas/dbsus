namespace DbSus.Client;

/// <summary>
/// A validated path on the DATASUS server, relative to the client's root.
/// </summary>
/// <remarks>
/// Paths are relative to <see cref="DatasusClientOptions.RootPath"/>; a leading <c>/</c> is
/// optional. A path whose first segment is the root's first segment (e.g. <c>/dissemin/...</c>)
/// is taken as absolute and must lie under the root. Every segment must be a name valid on both
/// Windows and Linux, because it is also used as a folder name in the local cache.
/// </remarks>
internal sealed class RemotePath
{
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '|', '?', '*', '\\'];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private RemotePath(string root, IReadOnlyList<string> segments)
    {
        Segments = segments;
        Relative = "/" + string.Join("/", segments);
        Full = segments.Count == 0 ? root : root + Relative;
    }

    /// <summary>Gets the validated segments, without the root.</summary>
    public IReadOnlyList<string> Segments { get; }

    /// <summary>Gets the path relative to the root, starting with <c>/</c>.</summary>
    public string Relative { get; }

    /// <summary>Gets the full path on the server.</summary>
    public string Full { get; }

    /// <summary>Gets the last segment (the file name), or an empty string for the root.</summary>
    public string Name => Segments.Count == 0 ? string.Empty : Segments[Segments.Count - 1];

    /// <summary>Gets the parent directory.</summary>
    public RemotePath Parent(string root) => new(root, [.. Segments.Take(Math.Max(0, Segments.Count - 1))]);

    /// <summary>Gets a sibling file in the same directory.</summary>
    public RemotePath Sibling(string root, string name) => new(root, [.. Segments.Take(Segments.Count - 1), name]);

    /// <summary>Validates and normalizes a path.</summary>
    /// <param name="path">The path given by the caller.</param>
    /// <param name="root">The client's root, e.g. <c>/dissemin/publicos</c>.</param>
    /// <param name="allowRoot">Whether the root itself (an empty path) is acceptable, as for listings.</param>
    /// <exception cref="ArgumentException">The path is not acceptable; the message says why.</exception>
    public static RemotePath Parse(string path, string root, bool allowRoot = false)
    {
        Guard.NotNull(path, nameof(path));
        root = NormalizeRoot(root);
        if (path.Contains("://"))
        {
            throw Invalid(path, "URLs are not accepted; give a path relative to the root");
        }

        if (path.Any(c => c < 0x20 || c == 0x7F))
        {
            throw Invalid(path, "it contains control characters");
        }

        if (path.Any(c => c == '\\'))
        {
            throw Invalid(path, "use '/' as the separator");
        }

        var rootSegments = root.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
        var relative = path;
        if (path.Length > 0 && path[0] == '/' && rootSegments.Length > 0)
        {
            var firstSegment = path.Substring(1).Split('/')[0];
            if (string.Equals(firstSegment, rootSegments[0], StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    && !path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                {
                    throw Invalid(path, $"it is an absolute path outside the root '{root}'");
                }

                relative = path.Substring(root.Length);
            }
        }

        relative = relative.TrimStart('/');
        if (relative.Length > 0 && relative[relative.Length - 1] == '/')
        {
            relative = relative.Substring(0, relative.Length - 1);
        }

        if (relative.Length == 0)
        {
            return allowRoot ? new RemotePath(root, []) : throw Invalid(path, "it names no file");
        }

        var segments = relative.Split('/');
        foreach (var segment in segments)
        {
            ValidateSegment(path, segment);
        }

        return new RemotePath(root, segments);
    }

    private static string NormalizeRoot(string root)
    {
        var trimmed = root.TrimEnd('/');
        return trimmed.Length == 0 ? string.Empty : trimmed;
    }

    private static void ValidateSegment(string path, string segment)
    {
        if (segment.Length == 0)
        {
            throw Invalid(path, "it contains an empty segment ('//')");
        }

        if (segment is "." or "..")
        {
            throw Invalid(path, "'.' and '..' segments are not accepted");
        }

        if (segment.Any(InvalidCharacters.Contains))
        {
            throw Invalid(path, $"segment '{segment}' contains a character invalid in file names");
        }

        var last = segment[segment.Length - 1];
        if (last == '.' || last == ' ' || segment[0] == ' ')
        {
            throw Invalid(path, $"segment '{segment}' starts or ends with a space or ends with a dot");
        }

        var stem = segment.Split('.')[0];
        if (ReservedNames.Contains(stem))
        {
            throw Invalid(path, $"segment '{segment}' is a reserved device name on Windows");
        }

        if (segment.Length > 255)
        {
            throw Invalid(path, "a segment is longer than 255 characters");
        }
    }

    private static ArgumentException Invalid(string path, string reason) =>
        new($"Invalid DATASUS path '{path}': {reason}.", nameof(path));
}
