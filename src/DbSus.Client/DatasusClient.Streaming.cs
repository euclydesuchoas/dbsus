using System.Diagnostics.CodeAnalysis;
using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Client;

public sealed partial class DatasusClient
{
    private const string OverloadsJustification =
        "The overloads follow the shape of OpenReaderAsync (a path, a table, or a dataset, state and period, then the options and the token); any new parameter comes as a new overload whose first parameter has a type of its own, never as one more optional parameter.";

    // Readers from OpenStreamingReaderAsync that are open: each holds one place of the pool until
    // it is done, and at most MaxConnections - 1 may, so that one place stays free.
    private int _streamingReaders;

    /// <summary>
    /// Preview: this method's API and behavior may change before a stable release. Opens a table
    /// for reading straight from the server, without downloading it: every part of the table (see
    /// <see cref="FindPartsAsync"/>), in order, as one <see cref="DbfReader"/>.
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
    /// The reader holds one place among the client's connections until it is disposed, has read
    /// its last part, or has failed for good; after a checksum failure, until it is disposed. Once
    /// it is disposed or has read its last part, it no longer counts among the open readers, at
    /// once; a transfer of it still under way is closed without waiting for the server, and the
    /// place is free once that is done. If the client was disposed before, the connection is closed
    /// instead of going back to the client, and the last <see cref="DbfReader.Read"/> and the
    /// reader's <c>Dispose</c> can wait for the server's reply to that closing (<c>QUIT</c>), up to
    /// <see cref="DatasusClientOptions.ReadTimeout"/>. The connection in that place changes along the way (after the headers read ahead, and at each
    /// resumption). At most <see cref="DatasusClientOptions.MaxConnections"/> - 1 such readers are
    /// open at a time, so that one connection stays free for other operations; opening one more
    /// fails at once, and so does opening one when MaxConnections is 1. A buffer of about 1 MiB is
    /// held for each part read, until the reader is disposed.
    /// </para>
    /// <para>
    /// A transfer that drops or stalls is resumed on a new connection, with the client's retries
    /// and backoff, once the size and time on the server are checked again. The attempts are
    /// counted in a row: the count starts again once 1 MiB has been read since the last failure.
    /// <see cref="DatasusClientOptions.OnRetry"/> reports the retries of a part's transfer on a
    /// background thread, never on the thread that called <see cref="DbfReader.Read"/>; the first
    /// part's transfer starts during the opening, so its retries run there too. During the opening,
    /// the retries of the listing, of the sizes and times, and of the headers read ahead run in the
    /// opening's asynchronous flow, not necessarily on the thread that called this method. The
    /// reader's work in the background runs on threads of the thread pool.
    /// </para>
    /// <para>
    /// Failures of the opening come from this method. Once the reader is open, every failure comes
    /// from <see cref="DbfReader.Read"/>, after the records already returned:
    /// <see cref="DatasusRetriesExhaustedException"/>, <see cref="DatasusRemoteFileChangedException"/>,
    /// <see cref="DatasusRequestRefusedException"/>, <see cref="FileNotFoundException"/>,
    /// <see cref="InvalidDataException"/> (a checksum), or <see cref="ObjectDisposedException"/> when
    /// the client was disposed and a new transfer was needed. When the opening fails with
    /// <see cref="InvalidDataException"/> because a later part's header does not fit the table, it
    /// asks the server for the size and time of the parts after the first, to tell a part that
    /// changed; if that question fails, its exception (one of the types above, or
    /// <see cref="OperationCanceledException"/>) comes out instead of the
    /// <see cref="InvalidDataException"/>.
    /// </para>
    /// <para>
    /// <see cref="DbfReader.Read"/> blocks the calling thread while it waits for the server, and
    /// <c>ReadAsync</c> completes synchronously, as Read does.
    /// </para>
    /// <para>
    /// The reader moves its transfers forward on threads of the thread pool: while it opens it takes
    /// more than one of them at a time, and while it reads its transfer needs them too. With the
    /// pool at its defaults that can only delay the opening or the reading; with a fixed cap on the
    /// pool's threads, blocking on this method from a thread of the pool (for example with
    /// <c>GetAwaiter().GetResult()</c>), or calling <see cref="DbfReader.Read"/> from threads of the
    /// pool, can stay stopped, without an exception, until threads of the pool are free. Use
    /// <see langword="await"/> for the opening, and read from a thread outside the pool.
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> holds for the life of the reader: it cancels the opening
    /// and the reading. A <see cref="DbfReader.Read"/> that waits for the server then throws
    /// <see cref="OperationCanceledException"/> at once; records already decompressed in memory may
    /// still be returned first, and after them every read throws. A token with a short deadline
    /// meant only for the opening also cancels the reads that follow. If the transfer of the part
    /// being read already has every byte when it sees the cancellation, the reader keeps its place
    /// among the connections, and among the streaming readers the client allows, until it is
    /// disposed; disposing it always lets both go, so dispose it after a cancellation too.
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
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader. Dispose the reader after a cancellation too: if the transfer of the part being read already had every byte, only disposing it lets its place go.</param>
    /// <returns>A reader positioned before the first record, after every part's header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is not acceptable (the message says why). It is checked before the
    /// limit of readers, and nothing was sent to the server.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DatasusClientOptions.MaxConnections"/> is 1, or MaxConnections - 1 readers from
    /// this method are open already. Nothing was sent to the server.
    /// </exception>
    /// <exception cref="FileNotFoundException">No such file, or a part before the last one is missing.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">
    /// The first part changed on the server while the reader was being opened, or a later part
    /// changed so that its header no longer fits the table.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">The server refused a request permanently. It is not retried.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    /// <exception cref="InvalidDataException">A part's header is invalid, or the parts' schemas differ.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the opening.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = OverloadsJustification)]
    public async Task<DbfReader> OpenStreamingReaderAsync(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        _ = RemotePath.Parse(path, Root); // a path that is not acceptable throws before a place is taken
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
    /// Preview: this method's API and behavior may change before a stable release. Opens a table
    /// already found for reading straight from the server, without downloading it and without
    /// looking for the table again, as one <see cref="DbfReader"/> over exactly
    /// <see cref="DatasusTableInfo.Parts"/>, in order. Use it to read preliminary data; for final data,
    /// a table can also be opened by dataset, state and period, or with
    /// <see cref="OpenNationalStreamingReaderAsync"/>.
    /// </summary>
    /// <remarks>
    /// Everything said for
    /// <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/>
    /// holds: nothing is written to disk, the checksums are checked at the end of each part, the
    /// reader holds one place among the connections until it is done, and at most MaxConnections - 1
    /// such readers are open at a time. Failures of the opening come from this method; once the
    /// reader is open, every failure comes from <see cref="DbfReader.Read"/>, after the records
    /// already returned. <see cref="DbfReader.Read"/> blocks the calling thread while it waits for the
    /// server, and <c>ReadAsync</c> completes synchronously, as Read does. While it opens, the reader
    /// takes more than one thread of the thread pool at a time, and its transfer needs them while it
    /// reads: with a fixed cap on the pool's threads, blocking on this method, or calling
    /// <see cref="DbfReader.Read"/>, from threads of the pool can stay stopped, without an exception,
    /// until threads are free; use <see langword="await"/> for the opening, and read from a thread
    /// outside the pool. If the client was disposed before, the last read and the reader's
    /// <c>Dispose</c> can wait for the server's reply to the closing of its connection, up to
    /// <see cref="DatasusClientOptions.ReadTimeout"/>.
    /// <see cref="DatasusClientOptions.OnRetry"/> reports the retries of a part's transfer, the first
    /// part's included, on a background thread; during the opening, those of the sizes and times and
    /// of the headers read ahead come from the opening's asynchronous flow, not necessarily on the
    /// thread that called this method. <paramref name="cancellationToken"/> holds for the life of the
    /// reader, so a token with a short deadline meant only for the opening also cancels the reads that
    /// follow. If the transfer of the part being read already has every byte when it sees the
    /// cancellation, the reader keeps its place among the connections, and among the streaming
    /// readers the client allows, until it is disposed; disposing it always lets both go, so dispose
    /// it after a cancellation too. The reader is not thread-safe; disposing it from another thread
    /// makes a <see cref="DbfReader.Read"/> in progress end with <see cref="ObjectDisposedException"/>, and
    /// leaves the reader unusable.
    /// The version expected of each part is the size and time of <see cref="DatasusTableInfo.Parts"/>:
    /// a table from <see cref="FindTableAsync"/> or <see cref="FindNationalTableAsync"/> has them, so a
    /// change since the table was found is seen; the tables of <see cref="ListTablesAsync"/> and
    /// <see cref="ListNationalTablesAsync"/> carry no time, so the version expected is the one on the
    /// server at the opening. To read a new version, find the table again.
    /// </remarks>
    /// <param name="table">The table to open.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader. Dispose the reader after a cancellation too: if the transfer of the part being read already had every byte, only disposing it lets its place go.</param>
    /// <returns>A reader positioned before the first record, after every part's header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="InvalidOperationException">
    /// <see cref="DatasusClientOptions.MaxConnections"/> is 1, or MaxConnections - 1 readers from
    /// these methods are open already. Nothing was sent to the server.
    /// </exception>
    /// <exception cref="FileNotFoundException">A part is no longer on the server.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">
    /// The first part changed on the server since the table was found (for a table without times,
    /// as <see cref="ListTablesAsync"/> gives them, during the opening), or a later part changed so
    /// that its header no longer fits the table.
    /// </exception>
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

    /// <summary>
    /// Preview: this method's API and behavior may change before a stable release. Finds the table
    /// of one state and period in the folder of <b>final</b> data, as
    /// <see cref="OpenReaderAsync(DatasusDataset, DatasusUf, DatasusPeriod, DbfReadOptions, DbcReadOptions, CancellationToken)"/>
    /// does, and opens it for reading straight from the server, without downloading it, as
    /// <see cref="OpenStreamingReaderAsync(DatasusTableInfo, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/> does.
    /// </summary>
    /// <remarks>
    /// The arguments are checked first; then the reader takes its place among the
    /// <see cref="DatasusClientOptions.MaxConnections"/> - 1 streaming readers, before anything is
    /// sent to the server. Preliminary data is never opened here: find the table with
    /// <see cref="FindTableAsync"/>, check <see cref="DatasusTableInfo.IsPreliminary"/>, and pass it
    /// to the overload that takes a table. Once the table is found, everything said for
    /// <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/> holds.
    /// </remarks>
    /// <param name="dataset">The dataset, from <see cref="DatasusCatalog"/>.</param>
    /// <param name="uf">The state.</param>
    /// <param name="period">A month for a monthly dataset, a year for a yearly one.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader. Dispose the reader after a cancellation too: if the transfer of the part being read already had every byte, only disposing it lets its place go.</param>
    /// <returns>A reader positioned before the first record, after every part's header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">As for <see cref="FindTableAsync"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="FindTableAsync"/>.</exception>
    /// <exception cref="InvalidOperationException">As for the overload that takes a path. Nothing was sent to the server.</exception>
    /// <exception cref="FileNotFoundException">
    /// The server has no final table for that state and period; the message says so when only a
    /// preliminary one exists, as <see cref="OpenReaderAsync(DatasusDataset, DatasusUf, DatasusPeriod, DbfReadOptions, DbcReadOptions, CancellationToken)"/> does.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">As for <see cref="FindTableAsync"/>, and for the overload that takes a path.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">As for the overload that takes a path.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    /// <exception cref="InvalidDataException">A part's header is invalid, or the parts' schemas differ.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the opening.</exception>
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = OverloadsJustification)]
    public async Task<DbfReader> OpenStreamingReaderAsync(DatasusDataset dataset, DatasusUf uf, DatasusPeriod period, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        RequireFilesByState(dataset);
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = dataset.EraFor(period, nameof(period));
        return await OpenFinalStreamingAsync(dataset, era, era.FileName(ufName, period), uf, ufName, period, nameof(FindTableAsync), dbfOptions, dbcOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Preview: this method's API and behavior may change before a stable release. Finds the table
    /// for the whole country of one period in the folder of <b>final</b> data, as
    /// <see cref="OpenNationalReaderAsync"/> does, and opens it for reading straight from the server,
    /// without downloading it, as
    /// <see cref="OpenStreamingReaderAsync(DatasusTableInfo, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/> does.
    /// </summary>
    /// <remarks>
    /// The arguments are checked first; then the reader takes its place among the
    /// <see cref="DatasusClientOptions.MaxConnections"/> - 1 streaming readers, before anything is
    /// sent to the server. Preliminary data is never opened here: find the table with
    /// <see cref="FindNationalTableAsync"/>, check <see cref="DatasusTableInfo.IsPreliminary"/>, and
    /// pass it to the overload of <c>OpenStreamingReaderAsync</c> that takes a table. Once the table is found, everything said for
    /// <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/> holds.
    /// </remarks>
    /// <param name="dataset">The dataset, from <see cref="DatasusCatalog"/>.</param>
    /// <param name="period">A month for a monthly dataset, a year for a yearly one.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read of the reader. Dispose the reader after a cancellation too: if the transfer of the part being read already had every byte, only disposing it lets its place go.</param>
    /// <returns>A reader positioned before the first record, after its header has been read and checked.</returns>
    /// <exception cref="ObjectDisposedException">The client is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">As for <see cref="FindNationalTableAsync"/>.</exception>
    /// <exception cref="ArgumentException">As for <see cref="FindNationalTableAsync"/>.</exception>
    /// <exception cref="InvalidOperationException">As for <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/>. Nothing was sent to the server.</exception>
    /// <exception cref="FileNotFoundException">
    /// The server has no final table for the whole country for that period; the message says so
    /// when only a preliminary one exists, as <see cref="OpenNationalReaderAsync"/> does.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">As for <see cref="FindNationalTableAsync"/>, and for the reading.</exception>
    /// <exception cref="DatasusRemoteFileChangedException">As for <see cref="OpenStreamingReaderAsync(string, DbfReadOptions?, DbcReadOptions?, CancellationToken)"/>.</exception>
    /// <exception cref="DatasusRetriesExhaustedException">Transient failures persisted after every retry.</exception>
    /// <exception cref="InvalidDataException">The header is invalid.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the opening.</exception>
    public async Task<DbfReader> OpenNationalStreamingReaderAsync(DatasusDataset dataset, DatasusPeriod period, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        RequireFileForTheCountry(dataset);
        var era = dataset.EraFor(period, nameof(period));
        return await OpenFinalStreamingAsync(dataset, era, era.NationalFileName(period), uf: null, ufName: null, period, nameof(FindNationalTableAsync), dbfOptions, dbcOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets how many readers from OpenStreamingReaderAsync are open; tests check it.</summary>
    internal int OpenStreamingReaders => Volatile.Read(ref _streamingReaders);

    /// <summary>Gets how many places of the pool are free; tests check it.</summary>
    internal int FreeConnectionPlaces => _pool.FreePlaces;

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

    // The final table of one file name, read without a download: the place of a streaming reader
    // first, as for a path, then the table, found as OpenFinalCoreAsync finds it, with the message
    // that names the overload that takes a table. Preliminary data is never opened here.
    private async Task<DbfReader> OpenFinalStreamingAsync(
        DatasusDataset dataset, DatasusEra era, string fileName, DatasusUf? uf, string? ufName, DatasusPeriod period, string findMethod, DbfReadOptions? dbfOptions, DbcReadOptions? dbcOptions, CancellationToken cancellationToken)
    {
        var releaseReader = ReserveStreamingReader();
        DatasusTableInfo table;
        try
        {
            var found = await FindInAsync(dataset, era, era.Directory, fileName, uf, period, isPreliminary: false, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                DatasusRequestRefusedException? notListed = null;
                if (era.PreliminaryDirectory is not null)
                {
                    (var preliminary, notListed) = await FindPreliminaryAsync(dataset, era, era.PreliminaryDirectory, fileName, uf, period, cancellationToken).ConfigureAwait(false);
                    if (preliminary is not null)
                    {
                        throw new FileNotFoundException(
                            $"{dataset}{OfState(ufName)} for {period} is not published as final data: only the preliminary table {preliminary.Path} exists, and this method opens final data only. "
                            + $"To read preliminary data, find the table with {findMethod} and pass it to OpenStreamingReaderAsync(DatasusTableInfo).",
                            era.Directory + "/" + fileName);
                    }
                }

                throw NoTable(dataset, era, ufName, period, fileName, notListed);
            }

            table = found;
        }
        catch
        {
            releaseReader();
            throw;
        }

        return await OpenStreamingPartsAsync(table.Parts, dbfOptions, dbcOptions, releaseReader, cancellationToken).ConfigureAwait(false);
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

        // The part whose header is being read ahead, while it is; 0 afterward.
        var readingAhead = 0;
        try
        {
            var ahead = new List<byte[]> { Array.Empty<byte>() };
            for (var i = 1; i < files.Count; i++)
            {
                readingAhead = i;
                ahead.Add(await ReadHeaderAheadAsync(lease, files[i].Remote, files[i].Info, cancellationToken).ConfigureAwait(false));
            }

            readingAhead = 0;

            for (var i = 0; i < files.Count; i++)
            {
                streams.Add(new StreamingPartStream(session, _retry, files[i].Remote, files[i].Info, ahead[i], _options.RestartFromStartUpTo, cancellationToken));
            }

            // Opening the reader reads the first part's header from its transfer, synchronously, and
            // waits for its first bytes: on the thread pool, never on the caller's thread.
            DbcPart[] dbcParts = [.. streams.Select((stream, i) => new DbcPart(parts[i].Name, stream))];
            return await Task.Run(() => DbSusFile.OpenDbcReader(dbcParts, dbfOptions, dbcOptions), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
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

            if (exception is InvalidDataException invalid && files.Count > 1)
            {
                // A part after the first republished since the table was found, with another schema,
                // is a file that changed: its size and time tell it from parts that do not match.
                // Asked only on this way out: for the part whose header was read ahead, or, when the
                // headers were compared, for every part after the first (the first part's version is
                // checked before its transfer).
                await ThrowIfChangedAsync(readingAhead > 0 ? [files[readingAhead]] : files.Skip(1), invalid, cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    // Throws DatasusRemoteFileChangedException for the first of the parts whose size or time on the
    // server is not the one expected.
    private async Task ThrowIfChangedAsync(IEnumerable<(RemotePath Remote, RemoteFileInfo Info)> parts, InvalidDataException invalid, CancellationToken cancellationToken)
    {
        foreach (var (remote, expected) in parts)
        {
            var current = await GetRemoteInfoAsync(remote, cancellationToken).ConfigureAwait(false);
            if (!current.SameVersionAs(expected))
            {
                throw new DatasusRemoteFileChangedException(
                    $"{remote.Relative} changed on the server since the table was found: it had {expected.Size} bytes, modified {expected.LastModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC, and now has {current.Size}, modified {current.LastModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC, so its header does not fit the table: {invalid.Message}",
                    remote,
                    expected,
                    current,
                    invalid);
            }
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
