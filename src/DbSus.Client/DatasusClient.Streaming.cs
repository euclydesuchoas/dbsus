using System.Diagnostics.CodeAnalysis;
using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Client;

public sealed partial class DatasusClient
{
    private const string OverloadsJustification =
        "The two overloads follow the shape of OpenReaderAsync (a path or a table, then the options and the token); any new parameter comes as a new overload, never as one more optional parameter.";

    // Readers from OpenStreamingReaderAsync that are open: each holds one place of the pool until
    // it is done, and at most MaxConnections - 1 may, so that one place stays free.
    private int _streamingReaders;

    /// <summary>
    /// Opens a table for reading straight from the server, without downloading it: every part of
    /// the table (see <see cref="FindPartsAsync"/>), in order, as one <see cref="DbfReader"/>.
    /// Preview: the behavior may change before a stable release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is written to disk: the cache (<see cref="DatasusClientOptions.CacheDirectory"/>) and
    /// the temporary folder are not used, and <see cref="DatasusClientOptions.VerifyOnDownload"/>
    /// does not apply. Each part's CRC-32 is checked when that part has been read to its end, after
    /// its records were returned, so records are confirmed only when <see cref="DbfReader.Read"/>
    /// returns <see langword="false"/>; disposing the reader before the end skips the check.
    /// </para>
    /// <para>
    /// The reader holds one of the client's connections until it is disposed, has read its last
    /// part, or has failed. At most <see cref="DatasusClientOptions.MaxConnections"/> - 1 such
    /// readers are open at a time, so that one connection stays free for other operations; opening
    /// one more fails at once, and so does opening one when MaxConnections is 1.
    /// </para>
    /// <para>
    /// A transfer that drops or stalls is resumed on a new connection, with the client's retries
    /// and backoff, once the size and time on the server are checked again. During the opening,
    /// <see cref="DatasusClientOptions.OnRetry"/> runs in the caller's flow; afterward, it runs on
    /// the reader's background thread, never on the thread that called <see cref="DbfReader.Read"/>.
    /// The first part's transfer starts during the opening, so its retries already run on that thread.
    /// </para>
    /// <para>
    /// Failures of the opening come from this method. Once the reader is open, every failure comes
    /// from <see cref="DbfReader.Read"/>, after the records already returned:
    /// <see cref="DatasusRetriesExhaustedException"/>, <see cref="DatasusRemoteFileChangedException"/>,
    /// <see cref="DatasusRequestRefusedException"/>, <see cref="FileNotFoundException"/>,
    /// <see cref="InvalidDataException"/> (a checksum), or <see cref="ObjectDisposedException"/> when
    /// the client was disposed and a new transfer was needed.
    /// </para>
    /// <para>
    /// <see cref="DbfReader.Read"/> blocks the calling thread while it waits for the server, and
    /// <c>ReadAsync</c> completes synchronously, as Read does.
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> holds for the life of the reader: it cancels the opening
    /// and every later read, which then throws <see cref="OperationCanceledException"/>. A token with
    /// a short deadline meant only for the opening also cancels the reads that follow.
    /// </para>
    /// <para>
    /// The reader is not thread-safe. Disposing it from another thread makes a
    /// <see cref="DbfReader.Read"/> in progress end with <see cref="ObjectDisposedException"/>, and
    /// leaves the reader unusable.
    /// </para>
    /// </remarks>
    /// <param name="path">Path of any part or of the unsplit name.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader.</param>
    /// <returns>A reader positioned before the first record, after every part's header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DatasusClientOptions.MaxConnections"/> is 1, or MaxConnections - 1 readers from
    /// this method are open already. Nothing was sent to the server.
    /// </exception>
    /// <exception cref="FileNotFoundException">No such file, or a part before the last one is missing.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">The first part changed on the server while the reader was being opened.</exception>
    /// <exception cref="DatasusRequestRefusedException">The server refused a request permanently. It is not retried.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    /// <exception cref="InvalidDataException">A part's header is invalid, or the parts' schemas differ.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the opening.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = OverloadsJustification)]
    public async Task<DbfReader> OpenStreamingReaderAsync(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        var releaseReader = ReserveStreamingReader();
        IReadOnlyList<DatasusFileInfo> parts;
        try
        {
            parts = await FindPartsAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            releaseReader();
            throw;
        }

        return await OpenStreamingPartsAsync(parts, dbfOptions, dbcOptions, releaseReader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a table already found for reading straight from the server, without downloading it and
    /// without looking for the table again, as one <see cref="DbfReader"/> over exactly
    /// <see cref="DatasusTableInfo.Parts"/>, in order. Use it to read preliminary data and the tables
    /// for the whole country. Preview: the behavior may change before a stable release.
    /// </summary>
    /// <remarks>
    /// Everything said for
    /// <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/>
    /// holds: nothing is written to disk, the checksums are checked at the end of each part, the
    /// reader holds one connection until it is done, and at most MaxConnections - 1 such readers are
    /// open at a time. Failures of the opening come from this method; once the reader is open, every
    /// failure comes from <see cref="DbfReader.Read"/>, after the records already returned.
    /// <see cref="DbfReader.Read"/> blocks the calling thread while it waits for the server, and
    /// <c>ReadAsync</c> completes synchronously, as Read does. During the opening,
    /// <see cref="DatasusClientOptions.OnRetry"/> runs in the caller's flow; afterward, it runs on the
    /// reader's background thread. <paramref name="cancellationToken"/> holds for the life of the
    /// reader, so a token with a short deadline meant only for the opening also cancels the reads that
    /// follow. The reader is not thread-safe; disposing it from another thread makes a
    /// <see cref="DbfReader.Read"/> in progress end with <see cref="ObjectDisposedException"/>, and
    /// leaves the reader unusable.
    /// </remarks>
    /// <param name="table">The table to open.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader.</param>
    /// <returns>A reader positioned before the first record, after every part's header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DatasusClientOptions.MaxConnections"/> is 1, or MaxConnections - 1 readers from
    /// these methods are open already. Nothing was sent to the server.
    /// </exception>
    /// <exception cref="FileNotFoundException">A part is no longer on the server.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">The first part changed on the server since the table was found.</exception>
    /// <exception cref="DatasusRequestRefusedException">The server refused a request permanently. It is not retried.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    /// <exception cref="InvalidDataException">A part's header is invalid, or the parts' schemas differ.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the opening.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = OverloadsJustification)]
    public async Task<DbfReader> OpenStreamingReaderAsync(DatasusTableInfo table, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(table, nameof(table));
        var releaseReader = ReserveStreamingReader();
        return await OpenStreamingPartsAsync(table.Parts, dbfOptions, dbcOptions, releaseReader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets how many readers from OpenStreamingReaderAsync are open; tests check it.</summary>
    internal int OpenStreamingReaders => Volatile.Read(ref _streamingReaders);

    // Takes one of the MaxConnections - 1 places of streaming readers, at once or not at all, before
    // anything is sent to the server. The action given back lets it go, once, however often it runs.
    private Action ReserveStreamingReader()
    {
        var limit = _options.MaxConnections - 1;
        if (limit < 1)
        {
            throw new InvalidOperationException(
                "Reading without a download needs MaxConnections of 2 or more: the reader holds one connection while another stays free for listings and sizes. Set DatasusClientOptions.MaxConnections to 2 or more, or use OpenReaderAsync.");
        }

        for (var open = Volatile.Read(ref _streamingReaders); ; open = Volatile.Read(ref _streamingReaders))
        {
            if (open >= limit)
            {
                throw new InvalidOperationException(
                    $"{open} {(open == 1 ? "reader" : "readers")} from OpenStreamingReaderAsync {(open == 1 ? "is" : "are")} open, the most this client allows with MaxConnections = {_options.MaxConnections}: each holds a connection, and one stays free for other operations. Dispose a reader, or set DatasusClientOptions.MaxConnections higher (at most 4).");
            }

            if (Interlocked.CompareExchange(ref _streamingReaders, open + 1, open) == open)
            {
                break;
            }
        }

        var released = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                Interlocked.Decrement(ref _streamingReaders);
            }
        };
    }

    // The versions first (on any place of the pool), then one place for the reader's life: the
    // headers of the parts after the first are read on it, one after the other, and each part's
    // body later, when the reader gets to it. Until the session holds that place, a failure lets
    // the reservation go here; from then on, the session lets both go together.
    private async Task<DbfReader> OpenStreamingPartsAsync(IReadOnlyList<DatasusFileInfo> parts, DbfReadOptions? dbfOptions, DbcReadOptions? dbcOptions, Action releaseReader, CancellationToken cancellationToken)
    {
        var files = new List<(RemotePath Remote, RemoteFileInfo Info)>();
        ConnectionPool.Lease lease;
        try
        {
            foreach (var part in parts)
            {
                var remote = RemotePath.Parse(part.Path, Root);
                files.Add((remote, part.LastModifiedUtc is { } modified
                    ? new RemoteFileInfo(part.Size, modified)
                    : await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false)));
            }

            lease = await _pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            releaseReader();
            throw;
        }

        var session = new StreamingSession(lease, releaseReader);
        var streams = new List<StreamingPartStream>();
        try
        {
            var ahead = new List<byte[]> { Array.Empty<byte>() };
            for (var i = 1; i < files.Count; i++)
            {
                ahead.Add(await ReadHeaderAheadAsync(lease, files[i].Remote, files[i].Info, cancellationToken).ConfigureAwait(false));
            }

            for (var i = 0; i < files.Count; i++)
            {
                streams.Add(new StreamingPartStream(session, _retry, files[i].Remote, files[i].Info, ahead[i], _options.RestartFromStartUpTo, cancellationToken));
            }

            return DbSusFile.OpenDbcReader([.. streams.Select((stream, i) => new DbcPart(parts[i].Name, stream))], dbfOptions, dbcOptions);
        }
        catch
        {
            // The streams are the caller's to DbSus, which does not dispose them when opening fails.
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            if (streams.Count == 0)
            {
                session.Release();
            }

            throw;
        }
    }

    // The DBF header and the checksum after it (the first bytes of a .dbc), on the reader's place:
    // a new connection when the one there cannot be used, and the transfer aborted afterward. A
    // transfer that ends inside them is cut short when the size announced at the opening has them,
    // and is read again then, as any transient failure.
    private Task<byte[]> ReadHeaderAheadAsync(ConnectionPool.Lease lease, RemotePath remote, RemoteFileInfo info, CancellationToken cancellationToken) =>
        _retry.RunAsync($"Reading the header of {remote.Relative}", remote.Relative, async token =>
        {
            await lease.EnsureConnectionAsync(token).ConfigureAwait(false);
            try
            {
                using var stream = await lease.Connection.OpenReadAsync(remote.Full, 0, token).ConfigureAwait(false);
                var needed = 32 + 4;
                try
                {
                    var prefix = await ReadExactlyAsync(stream, 32, remote, token).ConfigureAwait(false);
                    var headerLength = prefix[8] | (prefix[9] << 8);
                    needed = Math.Max(32, headerLength) + 4;
                    var rest = await ReadExactlyAsync(stream, Math.Max(0, headerLength - 32) + 4, remote, token).ConfigureAwait(false);
                    return (byte[])[.. prefix, .. rest];
                }
                catch (InvalidDataException)
                {
                    // ReadExactlyAsync throws it when the transfer ends before the bytes asked for.
                    throw EndedInsideHeader(remote, needed, info.Size);
                }
                finally
                {
                    // Closed by force before the stream, so that disposing it does not wait for a reply.
                    lease.Abort();
                }
            }
            catch (FileNotFoundException)
            {
                throw;
            }
            catch
            {
                lease.Abort();
                throw;
            }
        }, cancellationToken);
}
