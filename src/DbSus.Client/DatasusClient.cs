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
/// <c>/SIASUS/200801_/Dados/PAMG2407a.dbc</c>. The FTP traffic is plain text (the server offers no
/// TLS). Integrity rests on the size reported by the server and on the CRC-32 inside each DBC,
/// which detects corruption but not deliberate tampering.
/// </para>
/// </remarks>
public sealed class DatasusClient : IDisposable, IAsyncDisposable
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
        string? tempRoot)
    {
        _options = Copy(options ?? new DatasusClientOptions());
        _options.Validate();
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _freeSpace = freeSpace ?? FreeSpace;
        _pool = new ConnectionPool(connections ?? new FtpConnectionFactory(_options), _options.MaxConnections);
        _retry = new RetryPolicy(_options.MaxRetries, _options.InitialRetryDelay, delay ?? ((wait, token) => Task.Delay(wait, token)));
        _cache = _options.CacheDirectory is null ? null : new CacheStore(_options.CacheDirectory, _options.MaxCacheSizeBytes, _utcNow);
        _temp = new TempStore(tempRoot ?? TempStore.DefaultRoot, _utcNow);
    }

    private string Root => _options.RootPath.TrimEnd('/');

    /// <summary>Lists a directory (files and subdirectories). Listings are reused for <see cref="DatasusClientOptions.ListingCacheDuration"/>.</summary>
    /// <param name="directory">Directory relative to the root, e.g. <c>/SIHSUS/200801_/Dados</c>; empty or <c>/</c> for the root.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The entries; their <see cref="DatasusFileInfo.LastModifiedUtc"/> is <see langword="null"/> (listing times are imprecise).</returns>
    public async Task<IReadOnlyList<DatasusFileInfo>> ListAsync(string directory, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(directory, Root, allowRoot: true);
        var entries = await ListInternalAsync(remote, cancellationToken).ConfigureAwait(false);
        var prefix = remote.Relative == "/" ? "/" : remote.Relative + "/";
        return [.. entries.Select(entry => new DatasusFileInfo(prefix + entry.Name, entry.Name, entry.Size, null, entry.IsDirectory))];
    }

    /// <summary>Gets a file's exact size and modification time (FTP <c>SIZE</c> and <c>MDTM</c>).</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public async Task<DatasusFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var info = await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
        return new DatasusFileInfo(remote.Relative, remote.Name, info.Size, info.LastModifiedUtc, isDirectory: false);
    }

    /// <summary>
    /// Lists the parts of a split table from the server's directory listing, which is the source of
    /// truth for how many parts exist (unlike a local folder, it shows when the last part is
    /// missing). Same rules as <see cref="DbSusFile.FindParts(string)"/>; the result carries each
    /// part's exact size and time.
    /// </summary>
    /// <param name="path">Path of any part or of the unsplit name, e.g. <c>/SIASUS/200801_/Dados/PAMG2407.dbc</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="FileNotFoundException">No such file, or a part before the last one is missing.</exception>
    /// <exception cref="IOException">Ambiguous names on the server.</exception>
    public async Task<IReadOnlyList<DatasusFileInfo>> FindPartsAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var (baseName, extension) = PartNaming.Parse(remote.Name, nameof(path));
        var parent = remote.Parent(Root);
        var listing = await ListInternalAsync(parent, cancellationToken).ConfigureAwait(false);
        var prefix = parent.Relative == "/" ? "/" : parent.Relative + "/";
        var names = PartNaming.Select(
            listing.Where(entry => !entry.IsDirectory).Select(entry => entry.Name),
            baseName,
            extension,
            (missing, message) => new FileNotFoundException(message, prefix + missing));

        var parts = new List<DatasusFileInfo>();
        foreach (var name in names)
        {
            var part = remote.Sibling(Root, name);
            var info = await GetRemoteInfoAsync(part, cancellationToken).ConfigureAwait(false);
            parts.Add(new DatasusFileInfo(part.Relative, name, info.Size, info.LastModifiedUtc, isDirectory: false));
        }

        return parts;
    }

    /// <summary>
    /// Reads only the header of a remote DBC or DBF (a few kilobytes): schema and record count,
    /// without downloading the file. The transfer is aborted once the header has arrived.
    /// </summary>
    public async Task<DbfHeader> ReadHeaderAsync(string path, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var remote = RemotePath.Parse(path, Root);
        var bytes = await _retry.RunAsync($"Reading the header of {remote.Relative}", async token =>
        {
            // The lease is not kept: aborting a transfer may leave the connection unusable.
            using var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            using var stream = await lease.Connection.OpenReadAsync(remote.Full, 0, token).ConfigureAwait(false);
            var prefix = await ReadExactlyAsync(stream, 32, remote, token).ConfigureAwait(false);
            var headerLength = prefix[8] | (prefix[9] << 8);
            var rest = headerLength > 32 ? await ReadExactlyAsync(stream, headerLength - 32, remote, token).ConfigureAwait(false) : [];
            return (byte[])[.. prefix, .. rest];
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
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file failed its CRC-32 check twice.</exception>
    /// <exception cref="IOException">Retries were exhausted, or there is not enough disk space.</exception>
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
    public async Task<DbfReader> OpenReaderAsync(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var parts = await FindPartsAsync(path, cancellationToken).ConfigureAwait(false);
        var locals = new List<string>();
        foreach (var part in parts)
        {
            var remote = RemotePath.Parse(part.Path, Root);
            var info = new RemoteFileInfo(part.Size, part.LastModifiedUtc!.Value);
            locals.Add(await DownloadVersionAsync(remote, info, cancellationToken).ConfigureAwait(false));
        }

        var streams = new List<Stream>();
        try
        {
            var fileOptions = FileOptions.SequentialScan | (_cache is null ? FileOptions.DeleteOnClose : FileOptions.None);
            foreach (var local in locals)
            {
                streams.Add(new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, fileOptions));
            }

            var callerCallback = dbcOptions?.ChecksumMismatch;
            var cache = _cache;
            var options = new DbcReadOptions
            {
                VerifyChecksum = dbcOptions?.VerifyChecksum ?? true,
                ChecksumMismatch = (index, name) =>
                {
                    if (cache is not null)
                    {
                        new CacheEntry(Path.GetDirectoryName(locals[index])!, Path.GetFileName(locals[index])).MarkInvalid();
                    }

                    callerCallback?.Invoke(index, name);
                },
            };
            return DbSusFile.OpenDbcReader(streams, [.. parts.Select(part => part.Name)], dbfOptions, options);
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

    /// <summary>Closes pooled connections. Readers already returned keep working.</summary>
    public void Dispose()
    {
        _disposed = true;
        _pool.Dispose();
    }

    /// <summary>Closes pooled connections. Readers already returned keep working.</summary>
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

    // Reads from a network stream, reporting connection failures as transient (worth retrying).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "netstandard2.0 has no Memory-based Stream overloads.")]
    private static async Task<int> ReadNetworkAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or TimeoutException or FluentFTP.Exceptions.FtpException
                                          && exception is not TransientTransportException)
        {
            throw new TransientTransportException($"The connection failed during a transfer: {exception.Message}", exception);
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
            (connection, token) => connection.ListAsync(directory.Full, token),
            cancellationToken).ConfigureAwait(false);
        _listings[directory.Full] = (now, entries);
        return entries;
    }

    private Task<RemoteFileInfo> GetRemoteInfoAsync(RemotePath remote, CancellationToken cancellationToken) =>
        WithConnectionAsync(
            $"Reading the size and date of {remote.Relative}",
            (connection, token) => connection.GetInfoAsync(remote.Full, token),
            cancellationToken);

    // One retried operation on one pooled connection, kept for reuse unless something went wrong
    // (a missing file is a normal answer and does not taint the connection).
    private Task<T> WithConnectionAsync<T>(string operation, Func<IDatasusConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken) =>
        _retry.RunAsync(operation, async token =>
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
        }
    }

    private async Task<string> DownloadToCacheAsync(CacheStore cache, RemotePath remote, RemoteFileInfo info, CancellationToken cancellationToken)
    {
        var entry = cache.EntryFor(remote, info);
        Directory.CreateDirectory(entry.Directory);
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
                FileMoves.Replace(entry.PartialPath, entry.DataPath);
                var now = _utcNow();
                new EntryMeta(remote.Relative, info.Size, info.LastModifiedUtc, now, now, _options.VerifyOnDownload && IsDbc(remote))
                    .WriteAtomically(entry.MetaPath);
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
        if (Interlocked.Exchange(ref _tempCleaned, 1) == 0)
        {
            Maintain(_temp.CleanOrphans);
        }

        var entry = _temp.Create(remote.Name);
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
            Maintain(() => Directory.Delete(entry.Directory, recursive: true));
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "netstandard2.0 has no Memory-based Stream overloads.")]
    private Task<bool> FetchAsync(RemotePath remote, RemoteFileInfo info, string partialPath, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        EnsureDiskSpace(Path.GetDirectoryName(partialPath)!, remote, Math.Max(0, info.Size - existing));
        return _retry.RunAsync($"Downloading {remote.Relative}", async token =>
        {
            var offset = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
            if (offset > info.Size)
            {
                FileLocks.TryDelete(partialPath);
                offset = 0;
            }

            using var lease = await _pool.AcquireAsync(token).ConfigureAwait(false);
            if (offset > 0)
            {
                // Resume only if the server still has the same version; otherwise start over.
                await EnsureSameVersionAsync(lease, remote, info, partialPath, token).ConfigureAwait(false);
            }

            if (offset < info.Size)
            {
                using var source = await lease.Connection.OpenReadAsync(remote.Full, offset, token).ConfigureAwait(false);
                using var target = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var buffer = new byte[81920];
                int read;
                while ((read = await ReadNetworkAsync(source, buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
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
    }

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
            throw new IOException(
                $"Not enough disk space in '{directory}' to download {remote.Relative}: {needed:N0} bytes needed plus a {DiskSpaceMargin:N0}-byte margin, {free:N0} available.");
        }
    }

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
