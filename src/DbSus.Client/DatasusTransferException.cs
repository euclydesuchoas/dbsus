using System.Diagnostics.CodeAnalysis;

namespace DbSus.Client;

/// <summary>
/// A transfer with the DATASUS server failed. The subclasses tell why: retries were exhausted
/// (<see cref="DatasusRetriesExhaustedException"/>) or the server refused the request
/// (<see cref="DatasusRequestRefusedException"/>). A missing file is a
/// <see cref="FileNotFoundException"/> instead (FTP 550 to a request for a file), never one of these.
/// Only the subclasses are thrown; catch this type to handle both.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with the transfer details; constructors without them would make no sense.")]
public abstract class DatasusTransferException : IOException
{
    private protected DatasusTransferException(string message, string? ftpReplyCode, Exception? innerException)
        : base(message, innerException)
    {
        FtpReplyCode = ftpReplyCode;
    }

    /// <summary>
    /// Gets the path of the file or directory the operation concerned, relative to
    /// <see cref="DatasusClientOptions.RootPath"/> and starting with <c>/</c> (<c>/</c> for the root
    /// itself). It is always set: when the failure happened while connecting, it is the path of the
    /// operation that needed the connection.
    /// </summary>
    public string RemotePath { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets the FTP reply code of the last failure (e.g. <c>421</c> or <c>530</c>), or
    /// <see langword="null"/> when there was none (dropped connection, timeout).
    /// </summary>
    public string? FtpReplyCode { get; }

    /// <summary>Gets the number of attempts made, the first one included.</summary>
    public int Attempts { get; internal set; }
}

/// <summary>
/// Transient failures (dropped connections, timeouts, FTP 4xx replies, truncated transfers, or an
/// exception that the FTP library is not meant to throw) persisted after
/// <see cref="DatasusClientOptions.MaxRetries"/> retries. The last failure is the
/// <see cref="Exception.InnerException"/>.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with the transfer details.")]
public sealed class DatasusRetriesExhaustedException : DatasusTransferException
{
    internal DatasusRetriesExhaustedException(string message, string? ftpReplyCode, Exception? innerException)
        : base(message, ftpReplyCode, innerException)
    {
    }
}

/// <summary>
/// The server refused the request with a permanent FTP reply (5xx), for example <c>530</c>, not
/// logged in. It is not retried. For a file, 550 is not one of these: it means that the file does
/// not exist, and is a <see cref="FileNotFoundException"/>. For a listing, 550 is one of these: FTP
/// uses it both for a folder that does not exist and for one that may not be read, so a refused
/// listing is never reported as a missing file; <see cref="DatasusTransferException.FtpReplyCode"/>
/// has the code and the message has the reply. The one exception is the folder of preliminary data
/// of a dataset of <see cref="DatasusCatalog"/>, which is optional: a 550 to its listing is taken
/// as there being no such folder.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with the transfer details.")]
public sealed class DatasusRequestRefusedException : DatasusTransferException
{
    internal DatasusRequestRefusedException(string message, string? ftpReplyCode, Exception? innerException)
        : base(message, ftpReplyCode, innerException)
    {
    }
}

/// <summary>
/// There is not enough free space to download a file. Checked before the transfer starts, against
/// the file's size plus a safety margin.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with the space figures.")]
public sealed class InsufficientDiskSpaceException : IOException
{
    internal InsufficientDiskSpaceException(string message, string directory, long requiredBytes, long availableBytes)
        : base(message)
    {
        Directory = directory;
        RequiredBytes = requiredBytes;
        AvailableBytes = availableBytes;
    }

    /// <summary>Gets the local directory the file was to be written to.</summary>
    public string Directory { get; }

    /// <summary>Gets the free space needed: the bytes still to download plus the safety margin.</summary>
    public long RequiredBytes { get; }

    /// <summary>Gets the free space available when the check was made.</summary>
    public long AvailableBytes { get; }
}
