using System.Runtime.ExceptionServices;

namespace DbSus.Client.Transport;

/// <summary>
/// What a reader from <c>OpenStreamingReaderAsync</c> holds from its opening to its end: one place
/// in the pool, with one connection at a time (the headers read ahead, then each part's body and
/// its resumptions, in turn), and the client's count of open streaming readers. Both are let go
/// once, whichever comes first: the reader is disposed, its last part is read, its opening fails,
/// or a part fails for good. Once every part is disposed (the reader is disposed, or its last part
/// is read), the count is let go at once and every transfer still under way is stopped, even one
/// that has every byte; the place is let go once the parts stop using the connection. A part that
/// fails for good lets both go once the part stops using it.
/// </summary>
internal sealed class StreamingSession
{
    private readonly ConnectionPool.Lease _lease;
    private readonly Action _releaseReader;

    // Every body started, so that the last part disposed can stop them all; guarded by itself.
    private readonly List<RemoteReadAheadStream> _bodies = [];
    private int _parts;
    private int _released;
    private int _readerReleased;
    private ExceptionDispatchInfo? _failure;
    private Task _current = Task.CompletedTask;

    public StreamingSession(ConnectionPool.Lease lease, Action releaseReader)
    {
        _lease = lease;
        _releaseReader = releaseReader;
    }

    public ConnectionPool.Lease Lease => _lease;

    /// <summary>Gets a value indicating whether the place and the count were let go.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>Counts the part streams; the session is let go when the last one is disposed.</summary>
    public void AddPart() => Interlocked.Increment(ref _parts);

    /// <summary>
    /// Starts the body of the next part: its pump uses the connection once the pump before it is
    /// over, and not at all when a part failed for good, whose failure it then ends with; letting go
    /// waits for it.
    /// </summary>
    public RemoteReadAheadStream StartPart(Func<Task, RemoteReadAheadStream> start)
    {
        var after = Volatile.Read(ref _current).ContinueWith(
            static (_, state) => ((StreamingSession)state!).ThrowIfFailed(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        var body = start(after);
        lock (_bodies)
        {
            _bodies.Add(body);
        }

        Volatile.Write(ref _current, body.Completion);
        return body;
    }

    public void PartDisposed()
    {
        if (Interlocked.Decrement(ref _parts) == 0)
        {
            // The reader is done with every part: it no longer counts among the open readers, even
            // while its connection is being closed, and the check one byte beyond the size of a part
            // that has every byte is not waited for.
            ReleaseReader();
            RemoteReadAheadStream[] bodies;
            lock (_bodies)
            {
                bodies = [.. _bodies];
            }

            foreach (var body in bodies)
            {
                body.Stop();
            }

            ReleaseWhenIdle();
        }
    }

    /// <summary>A part failed for good: the reader is of no more use, and holds nothing.</summary>
    public void Failed(Exception failure)
    {
        Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(failure), null);
        ReleaseWhenIdle();
    }

    /// <summary>Lets go now; only when nothing uses the connection (the opening failed).</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        try
        {
            _lease.Dispose();
        }
        finally
        {
            ReleaseReader();
        }
    }

    // Set before the failed pump ends, so a pump that waits for it sees it: the next part ends with
    // the same failure, which the reader may not have seen (one after the part's last record).
    private void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

    // The count of open readers is let go once, by whichever comes first.
    private void ReleaseReader()
    {
        if (Interlocked.Exchange(ref _readerReleased, 1) == 0)
        {
            _releaseReader();
        }
    }

    private void ReleaseWhenIdle() =>
        Volatile.Read(ref _current).ContinueWith(
            static (_, state) => ((StreamingSession)state!).Release(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
