using System.Globalization;
using System.Text;

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
    // The device names of Windows, the superscript digits one, two and three included, and the
    // console names.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
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
        if (CharactersProblem(path) is { } problem)
        {
            throw Invalid(path, problem);
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
            if (SegmentProblem(segment) is { } segmentProblem)
            {
                throw Invalid(path, segmentProblem);
            }
        }

        return new RemotePath(root, segments);
    }

    /// <summary>
    /// Checks a root for <see cref="DatasusClientOptions.RootPath"/> with the rules of
    /// <see cref="Parse"/>, since it is sent to the server in front of every path, and gives the
    /// message of what is wrong, or <see langword="null"/>. One <c>/</c> alone, the server's root,
    /// is accepted, and so is one <c>/</c> at the end.
    /// </summary>
    public static string? RootProblem(string? root)
    {
        if (string.IsNullOrEmpty(root) || root![0] != '/')
        {
            return "RootPath must start with '/'.";
        }

        var problem = CharactersProblem(root);
        var relative = NormalizeRoot(root);
        if (problem is null && relative.Length > 0)
        {
            problem = relative.Substring(1).Split('/').Select(SegmentProblem).FirstOrDefault(found => found is not null);
        }

        return problem is null ? null : $"Invalid RootPath '{Shown(root)}': {problem}.";
    }

    // What makes a path unacceptable as a whole, whatever its segments: the FTP command it goes in
    // must stay one line, and the FTP library refuses '%', ';' and some invisible characters in it.
    private static string? CharactersProblem(string path)
    {
        if (path.Contains("://"))
        {
            return "URLs are not accepted; give a path relative to the root";
        }

        for (var i = 0; i < path.Length; i++)
        {
            switch (char.GetUnicodeCategory(path, i))
            {
                case UnicodeCategory.Control:
                    return $"it contains control characters (U+{(int)path[i]:X4})";
                case UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator:
                    return $"it contains an invisible format or line separator character (U+{char.ConvertToUtf32(path, i):X4})";
            }

            if (char.IsHighSurrogate(path[i]))
            {
                i++;
            }
        }

        if (path.Any(c => c == '\\'))
        {
            return "use '/' as the separator";
        }

        if (path.Any(c => c is '%' or ';'))
        {
            return "'%' and ';' are not accepted, since the FTP library refuses them in a path";
        }

        return null;
    }

    private static string NormalizeRoot(string root)
    {
        var trimmed = root.TrimEnd('/');
        return trimmed.Length == 0 ? string.Empty : trimmed;
    }

    private static string? SegmentProblem(string segment)
    {
        if (segment.Length == 0)
        {
            return "it contains an empty segment ('//')";
        }

        if (segment is "." or "..")
        {
            return "'.' and '..' segments are not accepted";
        }

        if (segment.Any(InvalidCharacters.Contains))
        {
            return $"segment '{segment}' contains a character invalid in file names";
        }

        var last = segment[segment.Length - 1];
        if (last == '.' || last == ' ' || segment[0] == ' ')
        {
            return $"segment '{segment}' starts or ends with a space or ends with a dot";
        }

        // Windows ignores spaces at the end of the stem, so "CON .dbc" is the device too.
        var stem = segment.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem))
        {
            return $"segment '{segment}' is a reserved device name on Windows";
        }

        if (segment.Length > 255)
        {
            return "a segment is longer than 255 characters";
        }

        return null;
    }

    // A path as a message shows it: characters that are invisible or would break the line as \uXXXX.
    private static string Shown(string path)
    {
        var shown = new StringBuilder(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            var pair = char.IsSurrogatePair(path, i);
            if (char.GetUnicodeCategory(path, i) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                shown.Append("\\u").Append(char.ConvertToUtf32(path, i).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                shown.Append(path, i, pair ? 2 : 1);
            }

            if (pair)
            {
                i++;
            }
        }

        return shown.ToString();
    }

    private static ArgumentException Invalid(string path, string reason) =>
        new($"Invalid DATASUS path '{Shown(path)}': {reason}.", nameof(path));
}
