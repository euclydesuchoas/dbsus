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
/// The transfer stops at the size the server announced, and nothing beyond it is delivered. A
/// server that sends more makes the stream fail once the bytes up to the size have been read; one
/// that ends early makes it fail in place of the missing bytes. Both are reported as a download
/// without retries reports them, a <see cref="DatasusRetriesExhaustedException"/> after one
/// attempt: there is no resumption here.
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

    private readonly object _gate = new();
    private readonly byte[] _buffer = new byte[Capacity];
    private readonly ConnectionPool.Lease _lease;
    private readonly Stream _source;
    private readonly RemotePath _remote;
    private readonly long _size;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenRegistration _registration;

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
    private TaskCompletionSource<bool>? _spaceWaiter;

    private RemoteReadAheadStream(ConnectionPool.Lease lease, Stream source, RemotePath remote, long size, CancellationToken cancellationToken)
    {
        _lease = lease;
        _source = source;
        _remote = remote;
        _size = size;
        _cancellationToken = cancellationToken;
        _registration = cancellationToken.Register(OnCanceled);
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
    /// <param name="pool">The pool; the connection is held until the transfer ends or the stream is disposed.</param>
    /// <param name="remote">The file.</param>
    /// <param name="size">The size the server announced; the transfer must hold exactly that many bytes.</param>
    /// <param name="cancellationToken">Cancels the opening and, afterward, every read, for the life of the stream.</param>
    public static async Task<RemoteReadAheadStream> OpenAsync(ConnectionPool pool, RemotePath remote, long size, CancellationToken cancellationToken)
    {
        ConnectionPool.Lease? lease = null;
        Stream? source = null;
        try
        {
            lease = await pool.AcquireAsync(cancellationToken).ConfigureAwait(false);
            source = await lease.Connection.OpenReadAsync(remote.Full, 0, cancellationToken).ConfigureAwait(false);
            var stream = new RemoteReadAheadStream(lease, source, remote, size, cancellationToken);
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
        catch (TransientTransportException exception)
        {
            throw Exhausted(remote, exception);
        }
        catch (DatasusTransferException exception)
        {
            Stamp(exception, remote);
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
            lock (_gate)
            {
                first = !_disposed;
                _disposed = true;
                Monitor.PulseAll(_gate);
            }

            if (first)
            {
                // Waits for OnCanceled if it is running; it never waits for anything itself.
                _registration.Dispose();
                StopLater();
                Release();
            }
        }

        base.Dispose(disposing);
    }

    // Reads the transfer into the buffer until the size, checks that nothing follows, and gives the
    // connection back to the pool after the final reply; on any other way out, closes it by force.
    [SuppressMessage("Design", "CA1031", Justification = "Every failure of the pump goes to the consumer, which rethrows it; none may end the pump without telling it.")]
    private async Task PumpAsync()
    {
        var token = _stop.Token;
        var delivered = false;
        var reusable = false;
        try
        {
            for (var received = 0L; received < _size;)
            {
                var (offset, count) = await ReserveAsync((int)Math.Min(ChunkSize, _size - received), token).ConfigureAwait(false);
                var read = await ReadNetworkAsync(_source, _buffer, offset, count, token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TransientTransportException($"Received {received} of {_size} bytes of {_remote.Relative}.");
                }

                Publish(read);
                received += read;
            }

            // One byte beyond the size tells an exact end from a server that sends too much; that
            // byte, and anything after it, is never delivered.
            if (await ReadNetworkAsync(_source, new byte[1], 0, 1, token).ConfigureAwait(false) > 0)
            {
                throw new TransientTransportException(
                    $"The server sent more than the {_size} bytes of {_remote.Relative} that it announced; what came beyond them was not delivered.");
            }

            End(null);
            delivered = true;

            // Disposing the transfer reads the server's final reply (226); only then may the
            // connection be reused. A failure there is not the consumer's, who has every byte.
            _source.Dispose();
            _lease.Keep();
            reusable = true;
        }
        catch (Exception exception)
        {
            if (!delivered)
            {
                End(Classify(exception, token));
            }
        }
        finally
        {
            try
            {
                if (!reusable)
                {
                    // Closed by force, so that disposing a transfer left in the middle does not wait
                    // for a reply that the server may never send.
                    (_lease.Connection as IAbortableConnection)?.Abort();
                    DisposeQuietly(_source);
                }

                _lease.Dispose();
            }
            finally
            {
                Release();
            }
        }
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

    private void Publish(int count)
    {
        lock (_gate)
        {
            _count += count;
            _maxCount = Math.Max(_maxCount, _count);
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

    private Exception Classify(Exception exception, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            // Stopped by Dispose or by the caller's token: whatever the read threw on the way out.
            return new OperationCanceledException($"Reading {_remote.Relative} was canceled; the connection was closed.", exception, _cancellationToken);
        }

        switch (exception)
        {
            case TransientTransportException transient:
                return Exhausted(_remote, transient);
            case DatasusTransferException transfer:
                Stamp(transfer, _remote);
                return transfer;
            default:
                return exception;
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

    // As the retry policy reports a transient failure after its last attempt; here the only one.
    private static DatasusRetriesExhaustedException Exhausted(RemotePath remote, TransientTransportException exception) =>
        new($"Streaming {remote.Relative} failed after 1 attempt: {exception.Message}", exception.ReplyCode, exception.IsLibraryFailure ? exception.InnerException : exception)
        {
            RemotePath = remote.Relative,
            Attempts = 1,
        };

    private static void Stamp(DatasusTransferException exception, RemotePath remote)
    {
        if (exception.RemotePath.Length == 0)
        {
            exception.RemotePath = remote.Relative;
        }

        exception.Attempts = 1;
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
