namespace DbSus.Client;

/// <summary>What the bytes of a <see cref="DatasusProgressInfo"/> are.</summary>
public enum DatasusProgressStage
{
    /// <summary>
    /// The file is being downloaded, to the cache or to the temporary folder.
    /// <see cref="DatasusProgressInfo.BytesDone"/> counts the bytes of the local file.
    /// </summary>
    Downloading = 0,

    /// <summary>
    /// The downloaded <c>.dbc</c> is being checked (<see cref="DatasusClientOptions.VerifyOnDownload"/>).
    /// <see cref="DatasusProgressInfo.BytesDone"/> counts the bytes of the local file read so far.
    /// </summary>
    Verifying = 1,

    /// <summary>The file came from the cache, without a transfer: one report, with every byte done.</summary>
    FromCache = 2,

    /// <summary>
    /// The file is being read without a download (<c>OpenStreamingReaderAsync</c>).
    /// <see cref="DatasusProgressInfo.BytesDone"/> counts the bytes received from the server, up to
    /// about 1 MiB ahead of what the reader has consumed.
    /// </summary>
    Streaming = 3,
}

/// <summary>
/// Describes how far one file of an operation has gone, passed to
/// <see cref="DatasusFileRequest.Progress"/>.
/// </summary>
public sealed class DatasusProgressInfo
{
    internal DatasusProgressInfo(string operation, string remotePath, DatasusProgressStage stage, long bytesDone, long totalBytes, long resumedAt)
    {
        Operation = operation;
        RemotePath = remotePath;
        Stage = stage;
        BytesDone = bytesDone;
        TotalBytes = totalBytes;
        ResumedAt = resumedAt;
    }

    /// <summary>
    /// Gets a description of the operation for people to read, e.g.
    /// <c>Downloading /SIASUS/200801_/Dados/PAMG2407a.dbc</c>. Its wording may change; use
    /// <see cref="Stage"/> and <see cref="RemotePath"/> rather than parsing it.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Gets the path of the file, relative to <see cref="DatasusClientOptions.RootPath"/> and
    /// starting with <c>/</c>: for a table of several parts, the part this report is about.
    /// </summary>
    public string RemotePath { get; }

    /// <summary>Gets what <see cref="BytesDone"/> counts.</summary>
    public DatasusProgressStage Stage { get; }

    /// <summary>
    /// Gets the bytes of this file done so far, by the meaning of <see cref="Stage"/>. In a download it
    /// goes back to 0 when the download starts over (the file changed on the server, a resumption
    /// could not open, or the check failed). Reading without a download, it never goes back.
    /// </summary>
    public long BytesDone { get; }

    /// <summary>
    /// Gets the size of this file, as the server announced it: of the part, for a table of several
    /// parts. It changes when a download starts over with a new version of the file.
    /// </summary>
    public long TotalBytes { get; }

    /// <summary>
    /// Gets the byte the transfer under way asked the server to start from: 0 for a transfer from
    /// the start, and for the stages without a transfer, so a value above 0 tells a resumption.
    /// </summary>
    public long ResumedAt { get; }
}
