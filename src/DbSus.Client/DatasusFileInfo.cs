namespace DbSus.Client;

/// <summary>
/// A file or directory on the DATASUS server.
/// </summary>
public sealed class DatasusFileInfo
{
    internal DatasusFileInfo(string path, string name, long size, DateTime? lastModifiedUtc, bool isDirectory)
    {
        Path = path;
        Name = name;
        Size = size;
        LastModifiedUtc = lastModifiedUtc;
        IsDirectory = isDirectory;
    }

    /// <summary>Gets the path relative to the client's root, starting with <c>/</c>.</summary>
    public string Path { get; }

    /// <summary>Gets the file or directory name.</summary>
    public string Name { get; }

    /// <summary>Gets the size in bytes (0 for directories).</summary>
    public long Size { get; }

    /// <summary>
    /// Gets the exact modification time (FTP <c>MDTM</c>, UTC, to the second), or
    /// <see langword="null"/> for entries that come from a directory listing, whose times are
    /// imprecise (local server time, two-digit year, minutes).
    /// </summary>
    public DateTime? LastModifiedUtc { get; }

    /// <summary>Gets a value indicating whether the entry is a directory.</summary>
    public bool IsDirectory { get; }

    /// <inheritdoc/>
    public override string ToString() => Path;
}
