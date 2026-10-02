namespace DbSus.Client.Transport;

/// <summary>
/// Opens connections to the file server. The only implementation today is FTP (FluentFTP); the
/// shape (list, stat, read from an offset) also fits an HTTP transport with range requests, should
/// one ever exist. Tests replace it with an in-memory fake.
/// </summary>
internal interface IDatasusConnectionFactory
{
    Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One connection to the server. Not thread-safe: the client lends each connection to one
/// operation at a time. Failures are reported as <see cref="FileNotFoundException"/> (the file does
/// not exist), <see cref="TransientTransportException"/> (worth retrying) or any other exception
/// (permanent).
/// </summary>
internal interface IDatasusConnection : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the connection was closed by force (a cancellation or an
    /// expired read); such a connection is never reused.
    /// </summary>
    bool IsBroken { get; }

    Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken);

    Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the file for reading from <paramref name="offset"/>. The stream must be disposed before
    /// the connection is used again; disposing it before the end may leave the connection unusable.
    /// </summary>
    Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken);
}

internal sealed class RemoteEntry(string name, long size, bool isDirectory)
{
    public string Name { get; } = name;

    public long Size { get; } = size;

    public bool IsDirectory { get; } = isDirectory;
}

internal sealed class RemoteFileInfo(long size, DateTime lastModifiedUtc)
{
    public long Size { get; } = size;

    public DateTime LastModifiedUtc { get; } = lastModifiedUtc;

    public bool SameVersionAs(RemoteFileInfo other) => Size == other.Size && LastModifiedUtc == other.LastModifiedUtc;
}

/// <summary>A failure worth retrying: dropped connection, timeout, or an FTP 4xx reply.</summary>
internal sealed class TransientTransportException : IOException
{
    public TransientTransportException()
    {
    }

    public TransientTransportException(string message)
        : base(message)
    {
    }

    public TransientTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TransientTransportException(string message, string? replyCode, Exception? innerException = null)
        : base(message, innerException)
    {
        ReplyCode = replyCode;
    }

    /// <summary>Gets the FTP reply code (4xx), or null for a dropped connection or a timeout.</summary>
    public string? ReplyCode { get; }
}
