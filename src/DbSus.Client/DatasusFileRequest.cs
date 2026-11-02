using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Client;

/// <summary>
/// One call's request for a file or a table, for
/// <see cref="DatasusClient.OpenReaderAsync(DatasusFileRequest, CancellationToken)"/>,
/// <see cref="DatasusClient.OpenStreamingReaderAsync(DatasusFileRequest, CancellationToken)"/> and
/// <see cref="DatasusClient.DownloadAsync(DatasusFileRequest, CancellationToken)"/>: what to read,
/// the read options, and who receives the progress of that call alone.
/// </summary>
/// <remarks>
/// <para>
/// The call reads the properties once, when it starts. A request has either a path or a table. To
/// start from a dataset, a state and a period, find the table first
/// (<see cref="DatasusClient.FindTableAsync"/>, <see cref="DatasusClient.FindNationalTableAsync"/>).
/// </para>
/// <para>
/// <c>DownloadAsync</c> gives back one file and reads no record, so it takes neither what does not
/// apply to it: a request with a table, or with read options, fails with
/// <see cref="ArgumentException"/>, before anything is sent to the server.
/// </para>
/// </remarks>
public sealed class DatasusFileRequest
{
    /// <summary>Creates a request by path.</summary>
    /// <param name="path">
    /// Path relative to the root. Opening a reader, of any part or of the unsplit name; downloading,
    /// of the file itself.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    public DatasusFileRequest(string path)
    {
        Guard.NotNull(path, nameof(path));
        Path = path;
    }

    /// <summary>
    /// Creates a request by table, already found: a reader opens exactly its parts, without looking
    /// for the table again. <c>DownloadAsync</c> does not take it: make a request by path for each of
    /// <see cref="DatasusTableInfo.Parts"/>.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="table"/> is <see langword="null"/>.</exception>
    public DatasusFileRequest(DatasusTableInfo table)
    {
        Guard.NotNull(table, nameof(table));
        Table = table;
    }

    /// <summary>Gets the path, or <see langword="null"/> for a request by table.</summary>
    public string? Path { get; }

    /// <summary>Gets the table, or <see langword="null"/> for a request by path.</summary>
    public DatasusTableInfo? Table { get; }

    /// <summary>
    /// Gets or sets the DBF read options of a reader, or <see langword="null"/> for the defaults.
    /// <c>DownloadAsync</c> fails with <see cref="ArgumentException"/> when it is set.
    /// </summary>
    public DbfReadOptions? DbfOptions { get; set; }

    /// <summary>
    /// Gets or sets the DBC read options of a reader, or <see langword="null"/> for the defaults, as in
    /// the method's overloads that take them. <c>DownloadAsync</c> fails with
    /// <see cref="ArgumentException"/> when it is set.
    /// </summary>
    public DbcReadOptions? DbcOptions { get; set; }

    /// <summary>
    /// Gets or sets who receives the progress of this call, file by file, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How often:</b> at the start of each transfer, of each check and at each file from the cache;
    /// when a file reaches its size; and in between at most once every 250 ms, however fast the link
    /// is. A table of several parts reports part by part, with each part's own
    /// <see cref="DatasusProgressInfo.TotalBytes"/>; its sum is in <see cref="DatasusTableInfo.Parts"/>.
    /// A table republished while it is downloaded reports its parts again.
    /// </para>
    /// <para>
    /// <b>On which thread:</b> <see cref="IProgress{T}.Report"/> is called on the thread that moves the
    /// operation forward, usually one of the thread pool, one report at a time and in order. Reading
    /// without a download, that is always a thread of the pool, never the one that calls
    /// <see cref="DbSus.Dbf.DbfReader.Read"/>. It holds up the transfer while it runs, and with a cache
    /// also another call for the same file, so keep it short. <see cref="Progress{T}"/> instead posts
    /// each report to the context it was created in or to the pool: reports can then arrive out of
    /// order, and after the call has ended. If it throws, its exception is ignored and the operation
    /// goes on. A report can arrive just after a reader from <c>OpenStreamingReaderAsync</c> is
    /// disposed, which never waits for its transfer.
    /// </para>
    /// <para>
    /// <b>Two calls for the same file, with a cache:</b> the second waits for the entry, without any
    /// report while it waits, and receives one <see cref="DatasusProgressStage.FromCache"/> at the end.
    /// If the first call fails, the second goes on to download, and reports that.
    /// </para>
    /// <para>
    /// <b>Reading without a download:</b> the bytes counted are the ones received, not the ones
    /// the reader has consumed, and a later part reports when the reader gets to it.
    /// </para>
    /// </remarks>
    public IProgress<DatasusProgressInfo>? Progress { get; set; }
}
