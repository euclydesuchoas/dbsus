using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace DbSus.Client.Transport;

/// <summary>
/// A remote file read ahead of its consumer, over one pooled connection held until the transfer
/// ends: a pump reads the transfer asynchronously into a bounded buffer, and the consumer reads
/// that buffer synchronously, as the DbSus readers do. Only the pump reads the network, through
/// the transport's <see cref="ReadLimitStream"/>, so the read timeout and the cancellation hold
/// however the consumer reads.
/// </summary>
/// <remarks>
/// <para>
/// The transfer stops at the size the server announced, and nothing beyond it is delivered: a
/// server that sends more makes the stream fail, without resuming, once the bytes up to the size
/// have been read. A failure after the last byte, while the pump reads one more to see that the
/// transfer ends there (a reset, or a server that keeps the data connection open and silent until
/// the read timeout), fails nothing: every byte is in the buffer, so the stream ends as at an exact
/// end, without resuming, and the connection is closed instead of going back to the pool.
/// </para>
/// <para>
/// A transfer that drops, stalls past the read timeout or ends early before its last byte resumes,
/// on a new connection in the same place of the pool, from the end of what the pump has received:
/// the buffer keeps what it holds, and the consumer goes on reading it during the wait and the
/// reconnection. It resumes with REST, its opening limited by <see cref="Resumption.OpenTimeout"/>.
/// Only after a resumption of the same sequence failed to open does it ask for the file from byte 0
/// instead, dropping, unpublished, the bytes it already delivered: when the pump has received at
/// most the restart size (<see cref="Resumption.RestartFromStartUpTo"/> by default), or on the last
/// attempt the retry budget allows; otherwise it tries REST again. Before resuming, the size and
/// time on the server must still be those given at opening; otherwise the stream fails with
/// <see cref="DatasusRemoteFileChangedException"/>. The
/// attempts follow the client's retry policy (its backoff and OnRetry), counted in a row while
/// less than <see cref="ResetAfterBytes"/> arrive between two failures (bytes read again and
/// dropped are no progress); when they run out the
/// stream fails with <see cref="DatasusRetriesExhaustedException"/>. A file that is gone (550) and
/// a pool that was disposed end it too. Every failure comes after the bytes received before it.
/// </para>
/// <para>
/// One consumer reads at a time. <see cref="Stream.Dispose()"/> may come from another thread, and
/// so may the cancellation of the token given at opening, which holds for the life of the stream:
/// a blocked read then ends with <see cref="ObjectDisposedException"/> or
/// <see cref="OperationCanceledException"/>. Disposing never waits for the pump: it stops it, and
/// the pump closes the connection, by force if the transfer is not over, and frees its place in
/// the pool on its own. A stream that is never disposed may keep its connection.
/// </para>
/// </remarks>
internal sealed class RemoteReadAheadStream : Stream
{
    /// <summary>The size of the buffer: the most the pump reads ahead of the consumer.</summary>
    internal const int Capacity = 1024 * 1024;

    /// <summary>The most the pump asks the network for at once.</summary>
    internal const int ChunkSize = 64 * 1024;

    /// <summary>The bytes received between two failures that count as progress and restart the attempts.</summary>
    internal const int ResetAfterBytes = 1024 * 1024;

    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[Capacity];
    private readonly ConnectionPool.Lease _lease;
    private readonly RetryPolicy _retry;
    private readonly RemotePath _remote;
    private readonly RemoteFileInfo _expected;
    private readonly long _size;
    private readonly long _restartFromStartUpTo;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenRegistration _registration;

    // A stream on a lease that its owner holds for longer (a reader of several parts): the pump
    // waits for _after (the part before it is done with the connection), opens the transfer itself,
    // never gives the lease back, and tells the owner when it fails.
    private readonly bool _onLease;
    private readonly Task? _after;
    private readonly Action<Exception>? _onFailed;

    // The pump, the consumer and each queued stop; the last of them to finish disposes _stop, which
    // must not be disposed while another of them still uses it.
    private int _owners = 2;

    // Guarded by _gate. The buffer is a ring of _count bytes from _head on, wrapping at its end. The
    // pump writes only to the free part, outside the lock, and publishes what it wrote under it.
    private int _head;
    private int _count;
    private int _maxCount;
    private bool _ended;
    private ExceptionDispatchInfo? _failure;
    private bool _canceled;
    private bool _disposed;

    // Every byte up to the size was published: on a lease, disposing the stream then lets the pump
    // end the transfer on its own, so that the connection stays good for the next part.
    private bool _complete;
    private TaskCompletionSource<bool>? _spaceWaiter;

    // The pump's alone, after the opening: the transfer, which it replaces when it resumes, the
    // bytes it has published, where it resumes from, and, after a transfer that started over from
    // byte 0, how many of those the transfer has still to send again before anything new. The
    // lease's connection is the pump's as well.
    private Stream _source;
    private long _received;
    private long _toDrop;

    // Set by ResumeAsync when the opening of a resumption failed; the pump reads and clears it.
    private bool _openingFailed;

    // Set by DropAsync when a transfer from byte 0 has read again every byte already delivered; the
    // pump reads and clears it.
    private bool _caughtUp;

    // How a transfer that reached the size ended.
    private enum TransferEnd
    {
        // The server closed it there.
        Exact,

        // Reading beyond the size failed (a reset, or the read timeout): every byte is in all the same.
        ProbeFailed,

        // The server sent more than the size.
        Excess,
    }

    private RemoteReadAheadStream(ConnectionPool.Lease lease, Stream source, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, long restartFromStartUpTo, CancellationToken cancellationToken)
    {
        _lease = lease;
        _source = source;
        _retry = retry;
        _remote = remote;
        _expected = expected;
        _size = expected.Size;
        _restartFromStartUpTo = restartFromStartUpTo;
        _cancellationToken = cancellationToken;
        _registration = cancellationToken.Register(OnCanceled);
    }

    private RemoteReadAheadStream(ConnectionPool.Lease lease, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, long startAt, long restartFromStartUpTo, Task after, Action<Exception> onFailed, CancellationToken cancellationToken)
        : this(lease, Stream.Null, retry, remote, expected, restartFromStartUpTo, cancellationToken)
    {
        _onLease = true;
        _after = after;
        _onFailed = onFailed;
        _received = startAt;
    }

    public override bool CanRead
    {
        get
        {
            lock (_gate)
            {
                return !_disposed;
            }
        }
    }

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Gets the pump, which ends once the connection is back in the pool or closed; tests wait for it.</summary>
    internal Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Gets the most bytes the buffer has held at once; tests check it.</summary>
    internal int MaxBuffered
    {
        get
        {
            lock (_gate)
            {
                return _maxCount;
            }
        }
    }

    /// <summary>Opens a file from its start on a pooled connection and starts reading it ahead.</summary>
    /// <param name="pool">The pool; the place is held until the transfer ends or the stream is disposed.</param>
    /// <param name="retry">The retry policy, for the opening and for every resumption.</param>
    /// <param name="remote">The file.</param>
    /// <param name="expected">
    /// The size and time the server announced: the transfer must hold exactly that many bytes, and a
    /// resumption needs the server to announce the same again.
    /// </param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read, for the life of the stream.</param>
    public static Task<RemoteReadAheadStream> OpenAsync(ConnectionPool pool, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, CancellationToken cancellationToken) =>
        OpenAsync(pool, retry, remote, expected, Resumption.RestartFromStartUpTo, cancellationToken);

    /// <summary>Opens a file as <see cref="OpenAsync(ConnectionPool, RetryPolicy, RemotePath, RemoteFileInfo, CancellationToken)"/> does, with another restart size; tests change it.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="retry">The retry policy.</param>
    /// <param name="remote">The file.</param>
    /// <param name="expected">The size and time the server announced.</param>
    /// <param name="restartFromStartUpTo">Up to this many bytes received, a failed transfer starts over from byte 0 instead of resuming.</param>
    /// <param name="cancellationToken">Cancels the opening and every read.</param>
    internal static Task<RemoteReadAheadStream> OpenAsync(ConnectionPool pool, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, long restartFromStartUpTo, CancellationToken cancellationToken) =>
        retry.RunAsync(Operation(remote), remote.Relative, async token =>
        {
            ConnectionPool.Lease? lease = null;
            Stream? source = null;
            try
            {
                lease = await pool.AcquireAsync(token).ConfigureAwait(false);
                source = await lease.Connection.OpenReadAsync(remote.Full, 0, token).ConfigureAwait(false);
                var stream = new RemoteReadAheadStream(lease, source, retry, remote, expected, restartFromStartUpTo, cancellationToken);
                lease = null;
                source = null;
                // Never canceled before it starts: the pump is what gives the connection back.
                stream.Completion = Task.Run(stream.PumpAsync, CancellationToken.None);
                return stream;
            }
            catch (FileNotFoundException)
            {
                // A missing file is a normal answer and does not taint the connection.
                lease?.Keep();
                throw;
            }
            finally
            {
                if (source is not null)
                {
                    DisposeQuietly(source);
                }

                lease?.Dispose();
            }
        }, cancellationToken);

    /// <summary>
    /// Starts reading a file from <paramref name="startAt"/> on a lease that the caller holds and
    /// keeps (a reader of several parts, one at a time): the bytes before it are the caller's. The
    /// pump waits for <paramref name="after"/> first, and a fault of it is the stream's failure, as it
    /// is; then it opens the transfer itself, as its first attempt, on the lease's connection when it
    /// can still be used: the version is checked first, and a disposed pool fails it with
    /// <see cref="ObjectDisposedException"/>. That transfer starts from byte 0, without REST, and
    /// drops the bytes before <paramref name="startAt"/>, which are neither delivered again nor
    /// counted as progress; REST comes only with a resumption after a failure. The lease is never
    /// given back here; when the stream fails for good, <paramref name="onFailed"/> runs once, with
    /// the failure, at the end of the pump. Disposing the stream is no failure: before the last byte
    /// arrived, it stops the pump; after, the pump ends the transfer on its own.
    /// </summary>
    /// <param name="lease">The lease, held by the caller until it is done with every part.</param>
    /// <param name="retry">The retry policy, for the opening and for every resumption.</param>
    /// <param name="remote">The file.</param>
    /// <param name="expected">The size and time the server announced.</param>
    /// <param name="startAt">The first byte to read; the caller has the ones before it.</param>
    /// <param name="restartFromStartUpTo">Up to this many bytes received, a failed transfer may start over from byte 0.</param>
    /// <param name="after">Ends when nothing else uses the lease's connection any more.</param>
    /// <param name="onFailed">Called once, with the failure, when the stream has failed for good.</param>
    /// <param name="cancellationToken">Cancels every read, for the life of the stream.</param>
    internal static RemoteReadAheadStream StartOnLease(ConnectionPool.Lease lease, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, long startAt, long restartFromStartUpTo, Task after, Action<Exception> onFailed, CancellationToken cancellationToken)
    {
        var stream = new RemoteReadAheadStream(lease, retry, remote, expected, startAt, restartFromStartUpTo, after, onFailed, cancellationToken);
        stream.Completion = Task.Run(stream.PumpAsync, CancellationToken.None);
        return stream;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Guard.NotNull(buffer, nameof(buffer));
        if (offset < 0 || count < 0 || count > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Offset and count do not describe a valid range of the buffer.");
        }

        TaskCompletionSource<bool>? space;
        int taken;
        lock (_gate)
        {
            while (true)
            {
                Guard.NotDisposed(_disposed, this);
                if (_canceled)
                {
                    throw new OperationCanceledException($"Reading {_remote.Relative} was canceled.", _cancellationToken);
                }

                if (count == 0)
                {
                    return 0;
                }

                if (_count > 0)
                {
                    break;
                }

                if (_ended)
                {
                    // The bytes received before a failure are delivered first.
                    _failure?.Throw();
                    return 0;
                }

                Monitor.Wait(_gate);
            }

            taken = Math.Min(count, _count);
            var first = Math.Min(taken, Capacity - _head);
            Buffer.BlockCopy(_buffer, _head, buffer, offset, first);
            Buffer.BlockCopy(_buffer, 0, buffer, offset + first, taken - first);
            _head = (_head + taken) % Capacity;
            _count -= taken;
            space = _spaceWaiter;
            _spaceWaiter = null;
        }

        // Outside the lock; the pump goes on on the thread pool, never on this thread.
        space?.TrySetResult(true);
        return taken;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            bool first;
            bool stop;
            lock (_gate)
            {
                first = !_disposed;
                _disposed = true;
                stop = !(_onLease && _complete);
                Monitor.PulseAll(_gate);
            }

            if (first)
            {
                // Waits for OnCanceled if it is running; it never waits for anything itself.
                _registration.Dispose();
                if (stop)
                {
                    StopLater();
                }

                Release();
            }
        }

        base.Dispose(disposing);
    }

    // Reads the transfer into the buffer until the size, resuming it after transient failures, checks
    // that nothing follows, and gives the connection back to the pool after the final reply; on any
    // other way out, including a check that failed after the last byte, closes it by force.
    [SuppressMessage("Design", "CA1031", Justification = "Every failure of the pump goes to the consumer, which rethrows it; none may end the pump without telling it.")]
    private async Task PumpAsync()
    {
        var token = _stop.Token;
        var failures = 0;
        var lastFailureAt = _received;
        var delivered = false;

        // The failure the pump ends with, unless Dispose stopped it; and an earlier part's, as it was.
        Exception? failedWith = null;
        Exception? earlier = null;
        var exact = false;
        var reusable = false;

        // The number of the attempt under way: the failures in a row before it, unless enough
        // arrived since the last of them to start the count again.
        int Attempt() => _received - lastFailureAt >= ResetAfterBytes ? 1 : failures + 1;

        try
        {
            if (_after is not null)
            {
                // The part before this one ends its transfer (and reads the final reply) after its
                // last byte was delivered: the connection is this pump's once that is done.
                try
                {
                    await _after.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    earlier = exception;
                    throw;
                }
            }

            // On a lease, the first transfer starts from byte 0 and drops the bytes the caller has
            // already: REST is sent only to resume after a failure.
            var fromStart = _onLease && _received > 0;

            // Whether a resumption of this sequence failed to open: the evidence that resuming does
            // not work here, which alone lets a resumption start over from byte 0. A failure in the
            // middle of a transfer that opened is none. The sequence ends with the count of
            // attempts, when enough arrives past the last failure: the next sequence must find its
            // own evidence, so a link that drops now and then keeps resuming with REST. The
            // evidence is spent too once the transfer that started over has read again what was
            // delivered: a failure beyond that resumes with REST, so only the attempt after the
            // failed opening starts over.
            var resumptionFailedToOpen = false;

            // On a lease, the pump opens the first transfer itself, as a resumption.
            var first = _onLease;
            for (var resume = _onLease; ; resume = true)
            {
                try
                {
                    if (resume)
                    {
                        var opening = first;
                        first = false;
                        await ResumeAsync(fromStart, opening, token).ConfigureAwait(false);
                    }

                    var end = await TransferAsync(token).ConfigureAwait(false);
                    if (end == TransferEnd.Excess)
                    {
                        // Not resumed: a server that sends more than the size may hold another version.
                        throw Exhausted(
                            _remote,
                            new TransientTransportException($"The server sent more than the {_size} bytes of {_remote.Relative} that it announced; what came beyond them was not delivered."),
                            Attempt());
                    }

                    exact = end == TransferEnd.Exact;
                    break;
                }
                catch (TransientTransportException failure) when (!token.IsCancellationRequested)
                {
                    var newSequence = _received - lastFailureAt >= ResetAfterBytes;
                    failures = Attempt();
                    lastFailureAt = _received;
                    resumptionFailedToOpen = (resumptionFailedToOpen && !newSequence && !_caughtUp) || _openingFailed;
                    _openingFailed = false;
                    _caughtUp = false;
                    if (failures > _retry.MaxRetries)
                    {
                        throw Exhausted(_remote, failure, failures);
                    }

                    CloseTransfer();
                    await _retry.WaitBeforeRetryAsync(Operation(_remote), _remote.Relative, failures, failure, token).ConfigureAwait(false);

                    // With that evidence, from byte 0 when little has arrived (that costs less than
                    // another resumption that may fail too), or when the next attempt is the last the
                    // budget allows; otherwise REST again.
                    fromStart = resumptionFailedToOpen && (_received <= _restartFromStartUpTo || failures == _retry.MaxRetries);
                }
            }

            End(null);
            delivered = true;

            // With the synchronous FluentFTP client (.NET Framework), disposing the transfer reads
            // the server's final reply (226). With the asynchronous one it only drops the data
            // connection, and FluentFTP reads the reply left on the control connection, as stale
            // data, before the next command on it (waiting up to 10 s). Either way the connection
            // may be reused. A failure here is not the consumer's, who has every byte.
            if (exact)
            {
                _source.Dispose();
                _lease.Keep();
                reusable = true;
            }
        }
        catch (Exception exception)
        {
            if (!delivered)
            {
                var classified = ReferenceEquals(exception, earlier) ? exception : Classify(exception, Attempt(), token);
                End(classified);
                failedWith = token.IsCancellationRequested && !CanceledByCaller() ? null : classified;
            }
        }
        finally
        {
            try
            {
                // Closed by force, so that disposing a transfer left in the middle does not wait for
                // a reply that the server may never send. After an earlier part's failure nothing was
                // opened, and the lease may be given back already: it is not touched.
                if (!reusable && earlier is null)
                {
                    CloseTransfer();
                }

                if (!_onLease)
                {
                    _lease.Dispose();
                }
                else if (failedWith is not null)
                {
                    _onFailed!(failedWith);
                }
            }
            finally
            {
                Release();
            }
        }
    }

    // Reads the transfer into the buffer up to the size, and tells how it ended there: one byte
    // beyond the size, never delivered, tells an exact end from a server that sends more.
    private async Task<TransferEnd> TransferAsync(CancellationToken token)
    {
        await DropAsync(token).ConfigureAwait(false);
        while (_received < _size)
        {
            var (offset, count) = await ReserveAsync((int)Math.Min(ChunkSize, _size - _received), token).ConfigureAwait(false);
            var read = await ReadNetworkAsync(_source, _buffer, offset, count, token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new TransientTransportException($"Received {_received} of {_size} bytes of {_remote.Relative}.");
            }

            Publish(read);
            _received += read;
        }

        try
        {
            return await ReadNetworkAsync(_source, new byte[1], 0, 1, token).ConfigureAwait(false) == 0 ? TransferEnd.Exact : TransferEnd.Excess;
        }
        catch (TransientTransportException) when (!token.IsCancellationRequested)
        {
            // Every byte is in, and nothing is left to resume; the connection is not trusted again.
            return TransferEnd.ProbeFailed;
        }
    }

    // After a transfer that started over from byte 0 (or, on a lease, the first one of a part whose
    // first bytes the caller has): reads again the bytes already delivered and drops them,
    // unpublished, so that what follows is new. They move neither _received nor the count of
    // attempts. The read never goes past them, so the check one byte beyond the size is the same
    // as after any other opening; a transfer that ends before them ended early.
    private async Task DropAsync(CancellationToken token)
    {
        if (_toDrop == 0)
        {
            return;
        }

        var scratch = new byte[ChunkSize];
        while (_toDrop > 0)
        {
            var read = await ReadNetworkAsync(_source, scratch, 0, (int)Math.Min(scratch.Length, _toDrop), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new TransientTransportException($"Received {_received - _toDrop} of {_size} bytes of {_remote.Relative}.");
            }

            _toDrop -= read;
        }

        _caughtUp = true;
    }

    // A new connection in the same place of the pool, the same version on the server, and the
    // transfer again: from the end of what was received, or from byte 0, with what was received to
    // drop again.
    private async Task ResumeAsync(bool fromStart, bool opening, CancellationToken token)
    {
        if (opening && _lease.IsPoolDisposed)
        {
            // The client is disposed: no new transfer starts, even on a connection still open.
            throw ClientDisposed(_remote);
        }

        try
        {
            if (opening)
            {
                await _lease.EnsureConnectionAsync(token).ConfigureAwait(false);
            }
            else
            {
                await _lease.ReplaceConnectionAsync(token).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException exception)
        {
            throw new ObjectDisposedException(
                $"Reading {_remote.Relative} could not resume at byte {_received}: the client's connections were closed, so no new connection is made. The bytes before it were delivered.",
                exception);
        }

        var current = await _lease.Connection.GetInfoAsync(_remote.Full, token).ConfigureAwait(false);
        if (!current.SameVersionAs(_expected))
        {
            throw new DatasusRemoteFileChangedException(
                $"{_remote.Relative} changed on the server while it was read: it had {_expected.Size} bytes, modified {_expected.LastModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC, and now has {current.Size}, modified {current.LastModifiedUtc:yyyy-MM-dd HH:mm:ss} UTC. The bytes before byte {_received} were delivered and are from the earlier version.",
                _remote,
                _expected,
                current);
        }

        _toDrop = fromStart ? _received : 0;
        var offset = fromStart ? 0 : _received;
        try
        {
            _source = await _lease.Connection.OpenReadAsync(_remote.Full, offset, token).ConfigureAwait(false);
        }
        catch (TransientTransportException) when (offset > 0 && !token.IsCancellationRequested)
        {
            // The opening of a resumption failed (its limit ran out, a reset, another failure):
            // the pump takes it as evidence.
            _openingFailed = true;
            throw;
        }
    }

    private static ObjectDisposedException ClientDisposed(RemotePath remote) =>
        new($"Reading {remote.Relative} could not start: the client was disposed, so no new transfer is started. The records before it were delivered.", innerException: null);

    // Closes the transfer and its connection by force; the place in the pool stays the lease's.
    private void CloseTransfer()
    {
        _lease.Abort();
        DisposeQuietly(_source);
        _source = Stream.Null;
    }

    // A free, contiguous part of the buffer for the next read; waits while the buffer is full.
    private async Task<(int Offset, int Count)> ReserveAsync(int wanted, CancellationToken token)
    {
        while (true)
        {
            Task space;
            lock (_gate)
            {
                if (_count < Capacity)
                {
                    if (_count == 0)
                    {
                        // Only the pump moves the start of an empty buffer, and only here, between two
                        // reads, when no part of it is reserved.
                        _head = 0;
                    }

                    var tail = (_head + _count) % Capacity;
                    var free = tail >= _head ? Capacity - tail : _head - tail;
                    return (tail, Math.Min(wanted, free));
                }

                _spaceWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                space = _spaceWaiter.Task;
            }

            await WhenSignaledAsync(space, token).ConfigureAwait(false);
        }
    }

    // Under the same lock as the bytes it makes visible: the consumer cannot have the last byte of
    // the file before _complete is set.
    private void Publish(int count)
    {
        lock (_gate)
        {
            _count += count;
            _maxCount = Math.Max(_maxCount, _count);
            _complete = _received + count >= _size;
            Monitor.PulseAll(_gate);
        }
    }

    private void End(Exception? failure)
    {
        lock (_gate)
        {
            _ended = true;
            _failure = failure is null ? null : ExceptionDispatchInfo.Capture(failure);
            Monitor.PulseAll(_gate);
        }
    }

    private Exception Classify(Exception exception, int attempts, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            // Stopped by Dispose or by the caller's token: whatever the read threw on the way out.
            return new OperationCanceledException($"Reading {_remote.Relative} was canceled; the connection was closed.", exception, _cancellationToken);
        }

        switch (exception)
        {
            case TransientTransportException transient:
                return Exhausted(_remote, transient, attempts);
            case DatasusTransferException transfer:
                Stamp(transfer, _remote, attempts);
                return transfer;
            default:
                return exception;
        }
    }

    private bool CanceledByCaller()
    {
        lock (_gate)
        {
            return _canceled;
        }
    }

    private void OnCanceled()
    {
        lock (_gate)
        {
            _canceled = true;
            Monitor.PulseAll(_gate);
        }

        StopLater();
    }

    // Canceling runs the pump's continuations, which may close the transfer and wait for the server,
    // on the canceling thread; queued, it never holds up Dispose or the caller's Cancel. The queued
    // stop owns _stop until it has run; when every owner has finished already, there is nothing to stop.
    private void StopLater()
    {
        if (TryAddOwner())
        {
            ThreadPool.QueueUserWorkItem(static state => ((RemoteReadAheadStream)state!).StopNow(), this);
        }
    }

    private void StopNow()
    {
        try
        {
            _stop.Cancel();
        }
        catch (AggregateException)
        {
            // A callback of the pump's own operations failed; the pump stops all the same.
        }
        finally
        {
            Release();
        }
    }

    // Adds an owner of _stop, unless the last one has disposed it already.
    private bool TryAddOwner()
    {
        for (var owners = Volatile.Read(ref _owners); owners > 0; owners = Volatile.Read(ref _owners))
        {
            if (Interlocked.CompareExchange(ref _owners, owners + 1, owners) == owners)
            {
                return true;
            }
        }

        return false;
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _owners) == 0)
        {
            _stop.Dispose();
        }
    }

    private static async Task WhenSignaledAsync(Task signal, CancellationToken token)
    {
        var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (token.Register(() => canceled.TrySetResult(true)))
        {
            await Task.WhenAny(signal, canceled.Task).ConfigureAwait(false);
        }

        token.ThrowIfCancellationRequested();
    }

    // Reads from the transfer, reporting connection failures as transient, as the download does.
    [SuppressMessage("Performance", "CA1835", Justification = "net472 and netstandard2.0 have no Memory-based Stream overloads.")]
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

    private static string Operation(RemotePath remote) => $"Streaming {remote.Relative}";

    // As the retry policy reports a transient failure after its last attempt.
    private static DatasusRetriesExhaustedException Exhausted(RemotePath remote, TransientTransportException exception, int attempts) =>
        new($"{Operation(remote)} failed after {attempts} {(attempts == 1 ? "attempt" : "attempts")}: {exception.Message}", exception.ReplyCode, exception.IsLibraryFailure ? exception.InnerException : exception)
        {
            RemotePath = remote.Relative,
            Attempts = attempts,
        };

    private static void Stamp(DatasusTransferException exception, RemotePath remote, int attempts)
    {
        if (exception.RemotePath.Length == 0)
        {
            exception.RemotePath = remote.Relative;
        }

        if (exception.Attempts == 0)
        {
            exception.Attempts = attempts;
        }
    }

    [SuppressMessage("Design", "CA1031", Justification = "Closing a transfer left in the middle may fail in many ways; the connection is discarded either way.")]
    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
