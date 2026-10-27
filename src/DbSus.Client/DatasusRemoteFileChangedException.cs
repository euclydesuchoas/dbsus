using System.Diagnostics.CodeAnalysis;
using DbSus.Client.Transport;

namespace DbSus.Client;

/// <summary>
/// A file changed on the server (its size or its modification time) while a reader from
/// <see cref="DatasusClient.OpenStreamingReaderAsync(string, DbSus.Dbf.DbfReadOptions?, DbSus.Dbc.DbcReadOptions?, CancellationToken)"/>
/// was reading it, so the records already returned and those that would follow belong to
/// different versions. The reader checks the size and time before each transfer of a part opens
/// or resumes; a change during a transfer that does not drop is not seen. It is thrown from
/// <see cref="DbSus.Dbf.DbfReader.Read"/>, after the records already returned, or from
/// OpenStreamingReaderAsync itself when the first part changed (a table found earlier that
/// changed since, or a part that changed while the reader was being opened), or when a later part
/// changed so that its header no longer fits the table. To read the new version, open the reader
/// again by path, or with the table found again: a <see cref="DatasusTableInfo"/> found earlier
/// keeps the size and time it was found with. The tables of
/// <see cref="DatasusClient.ListTablesAsync(DatasusDataset, DatasusPeriod, DatasusPeriod, DatasusUf?, CancellationToken)"/>
/// carry no time, so for them only a change after the opening is seen. A download never throws
/// it: it downloads the new version instead.
/// </summary>
[SuppressMessage("Design", "CA1032", Justification = "Created only by the client with both versions.")]
public sealed class DatasusRemoteFileChangedException : DatasusTransferException
{
    internal DatasusRemoteFileChangedException(string message, RemotePath remote, RemoteFileInfo expected, RemoteFileInfo current, Exception? innerException = null)
        : base(message, ftpReplyCode: null, innerException)
    {
        Expected = new DatasusFileInfo(remote.Relative, remote.Name, expected.Size, expected.LastModifiedUtc, isDirectory: false);
        Current = new DatasusFileInfo(remote.Relative, remote.Name, current.Size, current.LastModifiedUtc, isDirectory: false);
        RemotePath = remote.Relative;
    }

    /// <summary>Gets the file as it was when the reading started: its size and its modification time (UTC).</summary>
    public DatasusFileInfo Expected { get; }

    /// <summary>Gets the file as the server has it now: its size and its modification time (UTC).</summary>
    public DatasusFileInfo Current { get; }
}
