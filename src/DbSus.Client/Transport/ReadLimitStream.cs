namespace DbSus.Client.Transport;

/// <summary>
/// Limits each read of a transfer to the read timeout, and makes cancellation effective, by
/// aborting the connection: a read that would not end on its own (an asynchronous socket read on
/// .NET Framework observes neither FluentFTP's timeout nor the cancellation token) then fails, and
/// the connection is marked broken so the pool never reuses it.
/// </summary>
internal sealed class ReadLimitStream(Stream inner, TimeSpan limit, Action abort) : Stream
{
    // How long an aborted read may take to fail once its connection is closed.
    private static readonly TimeSpan AbortGrace = TimeSpan.FromSeconds(5);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadLimitedAsync(token => inner.ReadAsync(buffer, offset, count, token), cancellationToken);

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(ReadLimitedAsync(token => inner.ReadAsync(buffer, token).AsTask(), cancellationToken));
#endif

    // The client reads transfers asynchronously; a synchronous read is passed through unlimited.
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

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
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<int> ReadLimitedAsync(Func<CancellationToken, Task<int>> readAsync, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var read = readAsync(stop.Token);
        var expired = Task.Delay(limit, stop.Token);
        if (await Task.WhenAny(read, expired).ConfigureAwait(false) == read)
        {
            stop.Cancel();
            return await read.ConfigureAwait(false);
        }

        abort();
        await Task.WhenAny(read, Task.Delay(AbortGrace, CancellationToken.None)).ConfigureAwait(false);
        Observe(read);
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException($"No data was received for {limit.TotalSeconds:0.###} seconds; the connection was closed.");
    }

    // The aborted read fails, or ends later; its outcome no longer matters, but a fault is observed.
    private static void Observe(Task<int> read) =>
        read.ContinueWith(task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
