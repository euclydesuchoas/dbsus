using System.Collections.Concurrent;
using System.Net.Sockets;
using DbSus.Client.Caching;
using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Client;

/// <summary>
/// Reads DATASUS files from its public FTP server: listings, split-table discovery, header-only
/// reads, resumable downloads with retries, an optional local cache, and readers over the result.
/// </summary>
/// <remarks>
/// <para>
/// Thread-safe: one instance can be shared (for example as a singleton in dependency injection).
/// Connections are pooled up to <see cref="DatasusClientOptions.MaxConnections"/>; the limit applies
/// per instance, and separate processes cannot coordinate it.
/// </para>
/// <para>
/// Paths are relative to <see cref="DatasusClientOptions.RootPath"/>, e.g.
/// <c>/SIASUS/200801_/Dados/PAMG2407a.dbc</c>. The FTP traffic is plain text (the server lists
/// <c>AUTH TLS</c> but refuses it: in October 2026 it answered 534, "Local policy on server does not allow TLS secure connections"). Integrity rests on the size reported by the server and on the CRC-32 inside each DBC,
/// which detects corruption but not deliberate tampering.
/// </para>
/// </remarks>
public sealed partial class DatasusClient : IDisposable, IAsyncDisposable
{
    private const long DiskSpaceMargin = 64L * 1024 * 1024;
    private const int MaxVersionRestarts = 3;

    private readonly DatasusClientOptions _options;
    private readonly ConnectionPool _pool;
    private readonly RetryPolicy _retry;
    private readonly CacheStore? _cache;
    private readonly TempStore _temp;
    private readonly Func<string, long?> _freeSpace;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, IReadOnlyList<RemoteEntry> Entries)> _listings = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _tempCleaned;
    private volatile bool _disposed;

    /// <summary>Creates a client for the DATASUS FTP server.</summary>
    /// <param name="options">Options, or <see langword="null"/> for the defaults.</param>
    public DatasusClient(DatasusClientOptions? options = null)
        : this(options, null, null, null, null, null)
    {
    }

    internal DatasusClient(
        DatasusClientOptions? options,
        IDatasusConnectionFactory? connections,
        Func<TimeSpan, CancellationToken, Task>? delay,
        Func<string, long?>? freeSpace,
        Func<DateTime>? utcNow,
        string? temporaryDirectory)
    {
        _options = Copy(options ?? new DatasusClientOptions());
        _options.Validate();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _freeSpace = freeSpace ?? FreeSpace;
        _pool = new ConnectionPool(connections ?? new FtpConnectionFactory(_options), _options.MaxConnections);
        _retry = new RetryPolicy(_options.MaxRetries, _options.InitialRetryDelay, delay ?? ((wait, token) => Task.Delay(wait, token)), _options.OnRetry);
        _cache = _options.CacheDirectory is null ? null : new CacheStore(_options.CacheDirectory, _options.MaxCacheSizeBytes, _utcNow);
        _temp = new TempStore(temporaryDirectory ?? TempStore.DefaultDirectory, _utcNow);
    }

    private string Root => _options.RootPath.TrimEnd('/');

    /// <summary>Lists a directory (files and subdirectories). Listings are reused for <see cref="DatasusClientOptions.ListingCacheDuration"/>.</summary>
    /// <param name="directory">Directory relative to the root, e.g. <c>/SIHSUS/200801_/Dados</c>; empty or <c>/</c> for the root.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The entries; their <see cref="DatasusFileInfo.LastModifiedUtc"/> is <see langword="null"/> (listing times are imprecise).
    /// The names are the ones the server sends, as they are: they are checked only when they become a path
    /// (for example given to <see cref="DownloadAsync"/>), so a name may hold characters that no path accepts.
    /// </returns>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of the directory with a permanent reply (FTP 5xx, 550 included: the
    /// folder does not exist or may not be read). It is not retried.
    /// </exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    public async Task<IReadOnlyList<DatasusFileInfo>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(directory, Root, allowRoot: true);
        var entries = await ListInternalAsync(remote, cancellationToken).ConfigureAwait(false);
        var prefix = remote.Relative == "/" ? "/" : remote.Relative + "/";
        return [.. entries.Select(entry => new DatasusFileInfo(prefix + entry.Name, entry.Name, entry.Size, null, entry.IsDirectory))];
    }

    /// <summary>Gets a file's exact size and modification time (FTP <c>SIZE</c> and <c>MDTM</c>).</summary>
    /// <param name="path">Path of the file, relative to the root.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The file's path, name, size in bytes and modification time in UTC
    /// (<see cref="DatasusFileInfo.LastModifiedUtc"/> is always set here).
    /// </returns>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public async Task<DatasusFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var info = await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
        return new DatasusFileInfo(remote.Relative, remote.Name, info.Size, info.LastModifiedUtc, isDirectory: false);
    }

    /// <summary>
    /// Lists the parts of a split table from the server's directory listing (unlike a local folder,
    /// it shows when the last part is missing), then asks the server directly for the part after
    /// the last one listed, and for the file itself when the listing shows nothing, so that a stale
    /// listing cannot hide a part. Same rules as <see cref="DbSusFile.FindParts(string)"/>; the
    /// result carries each part's exact size and time.
    /// </summary>
    /// <param name="path">Path of any part or of the unsplit name, e.g. <c>/SIASUS/200801_/Dados/PAMG2407.dbc</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The parts ordered by letter (a single entry for a table that is not split), each with its
    /// exact size and modification time in UTC.
    /// </returns>
    /// <exception cref="FileNotFoundException">No such file, or a part before the last one is missing.</exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of the file's folder with a permanent reply (FTP 5xx, 550 included: the
    /// folder does not exist or may not be read). It is not retried.
    /// </exception>
    /// <exception cref="IOException">Ambiguous names on the server.</exception>
    public async Task<IReadOnlyList<DatasusFileInfo>> FindPartsAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        return await FindPartsOrNullAsync(remote, nameof(path), cancellationToken).ConfigureAwait(false)
            ?? throw NoFileOrParts(remote);
    }

    private FileNotFoundException NoFileOrParts(RemotePath remote)
    {
        var (baseName, extension) = PartNaming.Parse(remote.Name, "path");
        return new FileNotFoundException($"No file or parts found for '{baseName + extension}'.", remote.Sibling(Root, baseName + extension).Relative);
    }

    // The parts of a table, or null when the server has neither the file nor any part. A table
    // with a part missing before the last one is an error, not an absent table.
    private async Task<IReadOnlyList<DatasusFileInfo>?> FindPartsOrNullAsync(RemotePath remote, string paramName, CancellationToken cancellationToken)
    {
        var (baseName, extension) = PartNaming.Parse(remote.Name, paramName);
        var parent = remote.Parent(Root);
        var listing = await ListInternalAsync(parent, cancellationToken).ConfigureAwait(false);
        var prefix = parent.Relative == "/" ? "/" : parent.Relative + "/";
        var unsplitName = baseName + extension;
        string? missingName = null;
        List<string> names;
        try
        {
            names = PartNaming.Select(
                listing.Where(entry => !entry.IsDirectory).Select(entry => entry.Name),
                baseName,
                extension,
                (missing, message) =>
                {
                    missingName = missing;
                    return new FileNotFoundException(message, prefix + missing);
                });
        }
        catch (FileNotFoundException) when (missingName == unsplitName)
        {
            // The listing shows neither the file nor any part: ask for them before giving up.
            names = [];
        }

        var found = new Dictionary<string, RemoteFileInfo>(StringComparer.OrdinalIgnoreCase);
        var listed = names.Count;
        if (names.Count == 0 && await TryGetRemoteInfoAsync(remote.Sibling(Root, unsplitName), cancellationToken).ConfigureAwait(false) is { } unsplit)
        {
            names.Add(unsplitName);
            found[unsplitName] = unsplit;
        }
        else if (names.Count == 0 || !string.Equals(names[0], unsplitName, StringComparison.OrdinalIgnoreCase))
        {
            // A lettered table: the parts after the last one listed, if the listing missed them.
            var last = names.Count == 0 ? 'a' - 1 : names[names.Count - 1][baseName.Length];
            for (var letter = (char)(last + 1); letter is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'); letter++)
            {
                var name = baseName + letter + extension;
                if (await TryGetRemoteInfoAsync(remote.Sibling(Root, name), cancellationToken).ConfigureAwait(false) is not { } info)
                {
                    break;
                }

                names.Add(name);
                found[name] = info;
            }
        }

        if (names.Count == 0)
        {
            return null;
        }

        if (names.Count > listed)
        {
            _listings.TryRemove(parent.Full, out _);
        }

        var parts = new List<DatasusFileInfo>();
        foreach (var name in names)
        {
            var part = remote.Sibling(Root, name);
            var info = found.TryGetValue(name, out var known) ? known : await GetRemoteInfoAsync(part, cancellationToken).ConfigureAwait(false);
            parts.Add(new DatasusFileInfo(part.Relative, name, info.Size, info.LastModifiedUtc, isDirectory: false));
        }

        return parts;
    }

    /// <summary>
    /// Reads only the header of a remote DBC or DBF (a few kilobytes): schema and record count,
    /// without downloading the file. The transfer is aborted once the header has arrived.
    /// </summary>
    /// <param name="path">Path of the file, relative to the root.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The parsed DBF header: version, last update, record count, record length, LDID and fields.</returns>
    /// <remarks>
    /// When the transfer ends inside the header, the server is asked for the file's size and time,
    /// on the same connection, to tell a transfer cut short (retried) from a file that really ends
    /// inside its header.
    /// </remarks>
    /// <exception cref="FileNotFoundException">
    /// The file does not exist, also when it is removed before its size is asked after a transfer
    /// that ended inside the header.
    /// </exception>
    /// <exception cref="InvalidDataException">The file ends inside its header, or the header is invalid.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">
    /// Transient failures persisted after every retry; a transfer that ends inside the header of a
    /// longer file is one of them.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused a request permanently (FTP 5xx), for example the size or time asked after a
    /// transfer that ended inside the header. It is not retried.
    /// </exception>
    public async Task<DbfHeader> ReadHeaderAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var bytes = await _retry.RunAsync($"Reading the header of {remote.Relative}", remote.Relative, async token =>
        {
            // The lease is not kept: aborting a transfer may leave the connection unusable.
            using var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            var needed = 32;
            using (var stream = await lease.Connection.OpenReadAsync(remote.Full, 0, token).ConfigureAwait(false))
            {
                try
                {
                    var prefix = await ReadExactlyAsync(stream, 32, remote, token).ConfigureAwait(false);
                    var headerLength = prefix[8] | (prefix[9] << 8);
                    needed = Math.Max(32, headerLength);
                    var rest = headerLength > 32 ? await ReadExactlyAsync(stream, headerLength - 32, remote, token).ConfigureAwait(false) : [];
                    return (byte[])[.. prefix, .. rest];
                }
                catch (InvalidDataException)
                {
                    // ReadExactlyAsync throws it when the transfer ends before the bytes asked for.
                }
            }

            // The transfer ended inside the header: the size, asked once it is closed, tells why.
            var info = await lease.Connection.GetInfoAsync(remote.Full, token).ConfigureAwait(false);
            throw EndedInsideHeader(remote, needed, info.Size);
        }, cancellationToken).ConfigureAwait(false);
        return DbfHeader.Read(new MemoryStream(bytes));
    }

    /// <summary>
    /// Downloads a file and returns its local path: an entry of the cache when
    /// <see cref="DatasusClientOptions.CacheDirectory"/> is set (reused while the server's size and
    /// time are unchanged), otherwise a file under the system temporary directory that the caller
    /// should delete (see <see cref="DatasusClientOptions.CacheDirectory"/> for the location).
    /// </summary>
    /// <remarks>
    /// Interrupted transfers resume where they stopped, unless the file changed on the server in the
    /// meantime, in which case the download restarts from the beginning. With
    /// <see cref="DatasusClientOptions.VerifyOnDownload"/>, a DBC whose CRC-32 fails is downloaded
    /// once more and then reported, never cached.
    /// </remarks>
    /// <param name="path">Path of the file, relative to the root.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The full local path of the downloaded file.</returns>
    /// <exception cref="FileNotFoundException">The file does not exist (FTP 550; never retried).</exception>
    /// <exception cref="InvalidDataException">The file failed its CRC-32 check twice.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">
    /// Transient failures persisted after every retry, or the file changed on the server at every
    /// attempt to download it.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">The server refused the request permanently.</exception>
    /// <exception cref="InsufficientDiskSpaceException">There is not enough free disk space for the file.</exception>
    /// <exception cref="IOException">
    /// A file of the cache entry could not be written, for example because the path is too long on
    /// .NET Framework without long path support (see <see cref="DatasusClientOptions.CacheDirectory"/>).
    /// </exception>
    public async Task<string> DownloadAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var info = await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
        return await DownloadVersionAsync(remote, info, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads every part of a table (see <see cref="FindPartsAsync"/>), one after the other, and
    /// opens them as one <see cref="DbfReader"/>. Disposing the reader releases the local files
    /// (without a cache, the files under the system temporary directory are deleted; see
    /// <see cref="DatasusClientOptions.CacheDirectory"/>).
    /// </summary>
    /// <param name="path">Path of any part or of the unsplit name.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">
    /// DBC read options, or <see langword="null"/>. If a cached part fails its checksum while being
    /// read, its cache entry is marked invalid (and replaced on the next download) before your own
    /// <see cref="DbcReadOptions.ChecksumMismatch"/> callback, if any, is invoked.
    /// </param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <returns>
    /// A reader positioned before the first record, over every part in order, after every part's
    /// header has been checked. Each part's checksum is verified when that part has been read to
    /// its end.
    /// </returns>
    /// <exception cref="FileNotFoundException">No such file, or a part before the last one is missing.</exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of the file's folder with a permanent reply (FTP 5xx, 550 included: the
    /// folder does not exist or may not be read). It is not retried.
    /// </exception>
    public async Task<DbfReader> OpenReaderAsync(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var parts = await FindPartsAsync(path, cancellationToken).ConfigureAwait(false);
        return await OpenPartsAsync(parts, dbfOptions, dbcOptions, cancellationToken).ConfigureAwait(false);
    }

    // Downloads the parts, one after the other, and opens them as one reader. A part that comes
    // from a listing has no exact time: the server is asked for it, as before every download.
    private async Task<DbfReader> OpenPartsAsync(IReadOnlyList<DatasusFileInfo> parts, DbfReadOptions? dbfOptions, DbcReadOptions? dbcOptions, CancellationToken cancellationToken)
    {
        var locals = new List<string>();
        foreach (var part in parts)
        {
            var remote = RemotePath.Parse(part.Path, Root);
            var info = part.LastModifiedUtc is { } modified
                ? new RemoteFileInfo(part.Size, modified)
                : await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
            locals.Add(await DownloadVersionAsync(remote, info, cancellationToken).ConfigureAwait(false));
        }

        var streams = new List<Stream>();
        try
        {
            foreach (var local in locals)
            {
                streams.Add(_cache is null
                    ? new TemporaryDownloadStream(local)
                    : new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan));
            }

            var callerCallback = dbcOptions?.ChecksumMismatch;
            var cache = _cache;
            var options = new DbcReadOptions
            {
                VerifyChecksum = dbcOptions?.VerifyChecksum ?? true,
                ChecksumMismatch = mismatch =>
                {
                    if (cache is not null)
                    {
                        var local = locals[mismatch.PartIndex];
                        new CacheEntry(Path.GetDirectoryName(local)!, Path.GetFileName(local)).MarkInvalid();
                    }

                    callerCallback?.Invoke(mismatch);
                },
            };
            return DbSusFile.OpenDbcReader([.. streams.Select((stream, i) => new DbcPart(parts[i].Name, stream))], dbfOptions, options);
        }
        catch
        {
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Removes every cache entry that is not in use, together with its lock file, and the folders
    /// left empty. Entries open by a reader are skipped. Meant for a cache that no other process is
    /// downloading into at the same moment. Does nothing without a cache.
    /// </summary>
    public Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        return Task.Run(() => _cache?.Clear(), cancellationToken);
    }

    /// <summary>
    /// Removes orphans (partial downloads and temporary files older than 24 hours whose lock is
    /// free), entries marked invalid, and, above <see cref="DatasusClientOptions.MaxCacheSizeBytes"/>,
    /// the least recently used entries. Entries in use are never removed. Each entry's small
    /// <c>entry.lock</c> file and the folders that become empty are kept (a lock file must not
    /// vanish while another process may be waiting on it); <see cref="ClearCacheAsync"/> removes them.
    /// </summary>
    public Task PruneCacheAsync(CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        return Task.Run(
            () =>
            {
                _cache?.Prune(keep: null);
                _temp.CleanOrphans();
            },
            cancellationToken);
    }

    /// <summary>
    /// Closes pooled connections. Readers from OpenReaderAsync and files already downloaded keep
    /// working. A reader from OpenStreamingReaderAsync goes on with the transfer under way; going on
    /// to its next part, or resuming after a failure, needs a new transfer, so its Read then throws
    /// <see cref="ObjectDisposedException"/>, after the records already returned.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _pool.Dispose();
    }

    /// <summary>
    /// Closes pooled connections. Readers from OpenReaderAsync and files already downloaded keep
    /// working. A reader from OpenStreamingReaderAsync goes on with the transfer under way; going on
    /// to its next part, or resuming after a failure, needs a new transfer, so its Read then throws
    /// <see cref="ObjectDisposedException"/>, after the records already returned.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private static DatasusClientOptions Copy(DatasusClientOptions source) => new()
    {
        Host = source.Host,
        Port = source.Port,
        RootPath = source.RootPath,
        CacheDirectory = source.CacheDirectory,
        MaxCacheSizeBytes = source.MaxCacheSizeBytes,
        MaxConnections = source.MaxConnections,
        ConnectTimeout = source.ConnectTimeout,
        ReadTimeout = source.ReadTimeout,
        MaxRetries = source.MaxRetries,
        InitialRetryDelay = source.InitialRetryDelay,
        ListingCacheDuration = source.ListingCacheDuration,
        VerifyOnDownload = source.VerifyOnDownload,
        LockTimeout = source.LockTimeout,
        OnRetry = source.OnRetry,
        ResumeOpenTimeout = source.ResumeOpenTimeout,
        RestartFromStartUpTo = source.RestartFromStartUpTo,
    };

    private static long? FreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root!).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null; // unknown: skip the check rather than block the download
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, RemotePath remote, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await ReadNetworkAsync(stream, buffer, total, count - total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException($"{remote.Relative} ends inside its header.");
            }

            total += read;
        }

        return buffer;
    }

    // A transfer that ended inside the header, before the `needed` bytes it takes: cut short when the
    // file has them, a transient failure as any transfer that ends early, and invalid only when the
    // file really ends inside its header.
    private static Exception EndedInsideHeader(RemotePath remote, long needed, long size) =>
        size >= needed
            ? new TransientTransportException($"The transfer of {remote.Relative} ended inside its header, which takes {needed} of its {size} bytes.")
            : new InvalidDataException($"{remote.Relative} ends inside its header.");

    // Reads from a network stream, reporting connection failures as transient (worth retrying).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "net472 and netstandard2.0 have no Memory-based Stream overloads.")]
    private static async Task<int> ReadNetworkAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or FluentFTP.Exceptions.FtpException
                                          && exception is not TransientTransportException)
        {
            throw new TransientTransportException($"The connection failed during a transfer: {ServerText.Clean(exception.Message)}", exception);
        }
    }

    private static bool IsDbc(RemotePath remote) => string.Equals(Path.GetExtension(remote.Name), ".dbc", StringComparison.OrdinalIgnoreCase);

    private static bool VerifyDbc(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var dbf = DbcFile.OpenAsDbf(path);
            var buffer = new byte[81920];
            while (dbf.Read(buffer, 0, buffer.Length) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListInternalAsync(RemotePath directory, CancellationToken cancellationToken)
    {
        var now = _utcNow();
        if (_listings.TryGetValue(directory.Full, out var cached) && now - cached.FetchedUtc < _options.ListingCacheDuration)
        {
            return cached.Entries;
        }

        var entries = await WithConnectionAsync(
            $"Listing {directory.Relative}",
            directory.Relative,
            (connection, token) => connection.ListAsync(directory.Full, token),
            cancellationToken).ConfigureAwait(false);
        _listings[directory.Full] = (now, entries);
        return entries;
    }

    // The size and time of a file, or null when the server says it does not exist (550).
    private async Task<RemoteFileInfo?> TryGetRemoteInfoAsync(RemotePath remote, CancellationToken cancellationToken)
    {
        try
        {
            return await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private Task<RemoteFileInfo> GetRemoteInfoAsync(RemotePath remote, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            $"Reading the size and date of {remote.Relative}",
            remote.Relative,
            (connection, token) => connection.GetInfoAsync(remote.Full, token),
            cancellationToken);

    // One retried operation on one pooled connection, kept for reuse unless something went wrong
    // (a missing file is a normal answer and does not taint the connection).
    private Task<T> WithConnectionAsync<T>(string operation, string remotePath, Func<IDatasusConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        _retry.RunAsync(operation, remotePath, async token =>
        {
            using var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            try
            {
                var result = await action(lease.Connection, token).ConfigureAwait(false);
                lease.Keep();
                return result;
            }
            catch (FileNotFoundException)
            {
                lease.Keep();
                throw;
            }
        }, cancellationToken);

    private async Task<string> DownloadVersionAsync(RemotePath remote, RemoteFileInfo info, CancellationToken cancellationToken)
    {
        for (var restarts = 0; ; restarts++)
        {
            try
            {
                return _cache is null
                    ? await DownloadToTempAsync(remote, info, cancellationToken).ConfigureAwait(false)
                    : await DownloadToCacheAsync(_cache, remote, info, cancellationToken).ConfigureAwait(false);
            }
            catch (RemoteFileChangedException changed) when (restarts < MaxVersionRestarts)
            {
                info = changed.Current; // a new version: start again from zero, never resume across versions
            }
            catch (RemoteFileChangedException)
            {
                var attempts = restarts + 1;
                throw new DatasusRetriesExhaustedException(
                    $"Downloading {remote.Relative} failed after {attempts} attempts: the file changed on the server at every attempt.",
                    ftpReplyCode: null,
                    innerException: null)
                {
                    RemotePath = remote.Relative,
                    Attempts = attempts,
                };
            }
        }
    }

    private async Task<string> DownloadToCacheAsync(CacheStore cache, RemotePath remote, RemoteFileInfo info, CancellationToken cancellationToken)
    {
        var entry = cache.EntryFor(remote, info);
        if (!Directory.Exists(entry.Directory))
        {
            // Checked before the folder of the entry exists, so that a size the disk cannot hold
            // (the server announces it) leaves no folder behind; FetchAsync checks again, with the
            // bytes of a partial download, once the folder is there.
            EnsureDiskSpace(entry.Directory, remote, info.Size);
        }

        WriteCacheEntry(remote, () => Directory.CreateDirectory(entry.Directory));
        var gate = _gates.GetOrAdd(entry.Directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using (await FileLocks.AcquireAsync(entry.LockPath, _options.LockTimeout, cancellationToken).ConfigureAwait(false))
            {
                if (entry.IsValidFor(remote, info))
                {
                    entry.Touch(_utcNow());
                    return entry.DataPath;
                }

                if ((File.Exists(entry.DataPath) || entry.IsMarkedInvalid) && !entry.TryRemove())
                {
                    throw new IOException(
                        $"The cached copy of {remote.Relative} is invalid and still open by a reader; dispose the readers and try again.");
                }

                await FetchVerifiedAsync(remote, info, entry.PartialPath, cancellationToken).ConfigureAwait(false);
                WriteCacheEntry(remote, () => FileMoves.Replace(entry.PartialPath, entry.DataPath));
                var now = _utcNow();
                var meta = new EntryMeta(remote.Relative, info.Size, info.LastModifiedUtc, now, now, _options.VerifyOnDownload && IsDbc(remote));
                WriteCacheEntry(remote, () => meta.WriteAtomically(entry.MetaPath));
            }
        }
        finally
        {
            gate.Release();
        }

        Maintain(() =>
        {
            cache.RemoveOtherVersions(entry);
            cache.Prune(entry);
        });
        return entry.DataPath;
    }

    private async Task<string> DownloadToTempAsync(RemotePath remote, RemoteFileInfo info, CancellationToken cancellationToken)
    {
        var entry = _temp.Create(remote.Name);
        if (Interlocked.Exchange(ref _tempCleaned, 1) == 0)
        {
            // On the thread pool, so that a large temporary directory never delays the download. The
            // folder of this download is left out by name: it is new and in use, whatever the clock says.
            TempCleanup = StartCleanup(() => _temp.CleanOrphans(keep: entry.Directory));
        }

        try
        {
            using (FileLocks.TryAcquire(entry.LockPath))
            {
                await FetchVerifiedAsync(remote, info, entry.PartialPath, cancellationToken).ConfigureAwait(false);
                File.Move(entry.PartialPath, entry.DataPath);
            }

            return entry.DataPath;
        }
        catch
        {
            FileLocks.TryDelete(entry.PartialPath);
            TempStore.RemoveDownloadFolder(entry.Directory);
            throw;
        }
    }

    private async Task FetchVerifiedAsync(RemotePath remote, RemoteFileInfo info, string partialPath, CancellationToken cancellationToken)
    {
        for (var download = 1; ; download++)
        {
            await FetchAsync(remote, info, partialPath, cancellationToken).ConfigureAwait(false);
            if (!_options.VerifyOnDownload || !IsDbc(remote)
                || await Task.Run(() => VerifyDbc(partialPath, cancellationToken), cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            FileLocks.TryDelete(partialPath);
            if (download == 2)
            {
                throw new InvalidDataException(
                    $"{remote.Relative} failed its CRC-32 check after two downloads and was not kept; the file on the server may be corrupt.");
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "net472 and netstandard2.0 have no Memory-based Stream overloads.")]
    private Task<bool> FetchAsync(RemotePath remote, RemoteFileInfo info, string partialPath, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        EnsureDiskSpace(Path.GetDirectoryName(partialPath)!, remote, Math.Max(0, info.Size - existing));
        var sentTooMuch = false;

        // Whether an attempt of this call failed to open a resumption (a transfer from an offset
        // beyond 0): the evidence that resuming does not work here. A failure in the middle of a
        // transfer that opened is none. The attempts of a download are counted for the whole call,
        // without starting again after progress. The evidence is spent by the attempt that starts
        // over, once its transfer opened: only the next attempt starts over.
        var resumptionFailedToOpen = false;
        return _retry.RunAsync($"Downloading {remote.Relative}", remote.Relative, async (attempt, token) =>
        {
            var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (offset > info.Size)
            {
                FileLocks.TryDelete(partialPath);
                offset = 0;
            }

            using var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            if (offset > 0 || sentTooMuch)
            {
                // Go on with a partial file only if the server still has the same version; otherwise
                // start over. After a transfer that sent more than the size, the file may have grown:
                // ask again first.
                await EnsureSameVersionAsync(lease, remote, info, partialPath, token).ConfigureAwait(false);
            }

            // A partial file, from an earlier attempt or another process, is resumed with REST. Only
            // after a resumption failed to open is it downloaded again from byte 0, without REST:
            // when it is small (that costs less than another resumption that may fail too), or on
            // the last attempt the budget allows. One that cannot be deleted is resumed instead:
            // appending to it from byte 0 would corrupt it. Either way, the version was asked above.
            var startsOver = false;
            if (offset > 0 && resumptionFailedToOpen && (offset <= _options.RestartFromStartUpTo || attempt == _retry.MaxRetries + 1))
            {
                FileLocks.TryDelete(partialPath);
                offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
                startsOver = offset == 0;
            }

            if (offset < info.Size)
            {
                // The .partial is opened before the transfer, so a path that cannot be written fails
                // before any data is requested.
                bool more;
                using (var target = OpenPartial(remote, partialPath))
                using (var source = await OpenTransferAsync(lease, offset, token).ConfigureAwait(false))
                {
                    resumptionFailedToOpen &= !startsOver;
                    more = await CopyAtMostAsync(source, target, info.Size - offset, token).ConfigureAwait(false);
                }

                if (more)
                {
                    // What came beyond the size was never written; the connection is not reused.
                    sentTooMuch = true;
                    FileLocks.TryDelete(partialPath);
                    throw new TransientTransportException(
                        $"The server sent more than the {info.Size} bytes of {remote.Relative} that it announced; the partial download was discarded.");
                }
            }

            var length = new FileInfo(partialPath).Length;
            if (length != info.Size)
            {
                throw new TransientTransportException($"Received {length} of {info.Size} bytes of {remote.Relative}.");
            }

            // The file may have been republished while it was being transferred.
            await EnsureSameVersionAsync(lease, remote, info, partialPath, token).ConfigureAwait(false);
            lease.Keep();
            return true;
        }, cancellationToken);

        // Opens the transfer, and records a resumption that fails to open as the evidence above.
        async Task<Stream> OpenTransferAsync(ConnectionPool.Lease lease, long offset, CancellationToken token)
        {
            try
            {
                return await lease.Connection.OpenReadAsync(remote.Full, offset, token).ConfigureAwait(false);
            }
            catch (TransientTransportException) when (offset > 0 && !token.IsCancellationRequested)
            {
                resumptionFailedToOpen = true;
                throw;
            }
        }
    }

    // Copies at most `remaining` bytes and tells whether the source had more. It asks for one byte
    // beyond what remains, to tell an exact end from a server that sends too much; that byte, and
    // anything after it, is never written.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "net472 and netstandard2.0 have no Memory-based Stream overloads.")]
    private static async Task<bool> CopyAtMostAsync(Stream source, Stream target, long remaining, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            var count = (int)Math.Min(buffer.Length, remaining + 1);
            var read = await ReadNetworkAsync(source, buffer, 0, count, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            if (read > remaining)
            {
                return true;
            }

            await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private FileStream OpenPartial(RemotePath remote, string partialPath)
    {
        FileStream Open() => new(partialPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        return _cache is null ? Open() : WriteCacheEntry(remote, Open);
    }

    // Creates or writes a file of a cache entry. On .NET Framework without long path support, a path
    // that is too long fails with one of these two exceptions, whose messages do not say why.
    private static T WriteCacheEntry<T>(RemotePath remote, Func<T> write)
    {
        try
        {
            return write();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or PathTooLongException)
        {
            throw new IOException(
                $"Could not write the cache entry of {remote.Relative}: {exception.Message} On .NET Framework without long path support, the entry path may be too long: use a shorter CacheDirectory.",
                exception);
        }
    }

    private static void WriteCacheEntry(RemotePath remote, Action write) =>
        WriteCacheEntry(remote, () =>
        {
            write();
            return true;
        });

    private static async Task EnsureSameVersionAsync(ConnectionPool.Lease lease, RemotePath remote, RemoteFileInfo info, string partialPath, CancellationToken cancellationToken)
    {
        var current = await lease.Connection.GetInfoAsync(remote.Full, cancellationToken).ConfigureAwait(false);
        if (!current.SameVersionAs(info))
        {
            FileLocks.TryDelete(partialPath);
            lease.Keep();
            throw new RemoteFileChangedException(current);
        }
    }

    private void EnsureDiskSpace(string directory, RemotePath remote, long needed)
    {
        if (_freeSpace(directory) is { } free && free < needed + DiskSpaceMargin)
        {
            throw new InsufficientDiskSpaceException(
                $"Not enough disk space in '{directory}' to download {remote.Relative}: {needed:N0} bytes needed plus a {DiskSpaceMargin:N0}-byte margin, {free:N0} available.",
                directory,
                needed + DiskSpaceMargin,
                free);
        }
    }

    /// <summary>
    /// Gets the clean-up of the temporary folders that the first download without a cache started,
    /// or a completed task; tests wait for it.
    /// </summary>
    internal Task TempCleanup { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Runs a clean-up on the thread pool. Whatever it throws is swallowed, except the failures of
    /// the process itself, so that the task never ends faulted and no exception goes unobserved.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A clean-up must never fail a download nor leave an unobserved exception.")]
    internal static Task StartCleanup(Action clean) => Task.Run(
        () =>
        {
            try
            {
                clean();
            }
            catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException))
            {
            }
        },
        CancellationToken.None);

    // Housekeeping never fails the operation that triggered it.
    private static void Maintain(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class RemoteFileChangedException(RemoteFileInfo current) : Exception("The file changed on the server.")
    {
        public RemoteFileInfo Current { get; } = current;
    }
}
