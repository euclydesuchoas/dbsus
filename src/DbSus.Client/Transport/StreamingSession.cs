using System.Runtime.ExceptionServices;

namespace DbSus.Client.Transport;

/// <summary>
/// What a reader from <c>OpenStreamingReaderAsync</c> holds from its opening to its end: one place
/// in the pool, with one connection at a time (the headers read ahead, then each part's body and
/// its resumptions, in turn), and the client's count of open streaming readers. Both are let go
/// once, whichever comes first: the reader is disposed, its last part is read, its opening fails,
/// or a part fails for good. Letting go waits for the part being read, if any, to stop using the
/// connection.
/// </summary>
internal sealed class StreamingSession
{
    private readonly ConnectionPool.Lease _lease;
    private readonly Action _releaseReader;
    private int _parts;
    private int _released;
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
        Volatile.Write(ref _current, body.Completion);
        return body;
    }

    public void PartDisposed()
    {
        if (Interlocked.Decrement(ref _parts) == 0)
        {
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
            _releaseReader();
        }
    }

    // Set before the failed pump ends, so a pump that waits for it sees it: the next part ends with
    // the same failure, which the reader may not have seen (one after the part's last record).
    private void ThrowIfFailed() => Volatile.Read(ref _failure)?.Throw();

    private void ReleaseWhenIdle() =>
        Volatile.Read(ref _current).ContinueWith(
            static (_, state) => ((StreamingSession)state!).Release(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
