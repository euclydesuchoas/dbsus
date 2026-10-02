namespace DbSus.Client;

/// <summary>
/// Describes a retry, passed to <see cref="DatasusClientOptions.OnRetry"/> just before the client
/// waits and tries again.
/// </summary>
public sealed class DatasusRetryInfo
{
    internal DatasusRetryInfo(string operation, string remotePath, int attempt, TimeSpan delay, Exception exception, string? ftpReplyCode)
    {
        Operation = operation;
        RemotePath = remotePath;
        Attempt = attempt;
        Delay = delay;
        Exception = exception;
        FtpReplyCode = ftpReplyCode;
    }

    /// <summary>
    /// Gets a description of the operation for people to read, e.g.
    /// <c>Downloading /SIASUS/200801_/Dados/PAMG2407a.dbc</c>. Its wording may change; use
    /// <see cref="RemotePath"/> rather than parsing it.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Gets the path of the file or directory the operation concerns, relative to
    /// <see cref="DatasusClientOptions.RootPath"/> and starting with <c>/</c> (<c>/</c> for the root
    /// itself), as in <see cref="DatasusTransferException.RemotePath"/>.
    /// </summary>
    public string RemotePath { get; }

    /// <summary>Gets the number of the attempt that failed, starting at 1.</summary>
    public int Attempt { get; }

    /// <summary>Gets how long the client waits before the next attempt.</summary>
    public TimeSpan Delay { get; }

    /// <summary>
    /// Gets the transient failure of that attempt. Its type is internal to the client; use
    /// <see cref="FtpReplyCode"/> and the message rather than the type.
    /// </summary>
    public Exception Exception { get; }

    /// <summary>
    /// Gets the FTP reply code of that failure (a 4xx code such as <c>421</c>), or
    /// <see langword="null"/> for a dropped connection, a timeout or a truncated transfer.
    /// </summary>
    public string? FtpReplyCode { get; }
}
