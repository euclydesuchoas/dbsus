using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// The client's own limit on each read of a download, and the pool's refusal to keep a connection
// that was closed by force: neither depends on the FTP library observing timeouts or tokens.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1835", Justification = "The client reads through the array overload, the one netstandard2.0 has.")]
public class ReadLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 10_000)]
    public async Task ReadThatNeverEnds_TimesOutAndAbortsTheConnection()
    {
        using var inner = new NeverEndingStream();
        var aborted = 0;
        using var stream = new ReadLimitStream(inner, TimeSpan.FromMilliseconds(200), () => { aborted++; inner.Dispose(); });

        await Assert.ThrowsAsync<TimeoutException>(() => stream.ReadAsync(new byte[10], 0, 10, TestContext.Current.CancellationToken));

        Assert.Equal(1, aborted);
    }

    [Fact(Timeout = 10_000)]
    public async Task CanceledRead_AbortsTheConnectionAndThrowsOperationCanceled()
    {
        using var inner = new NeverEndingStream();
        var aborted = 0;
        using var stream = new ReadLimitStream(inner, TimeSpan.FromMinutes(1), () => { aborted++; inner.Dispose(); });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.ReadAsync(new byte[10], 0, 10, cancellation.Token));

        Assert.Equal(1, aborted);
    }

    [Fact]
    public async Task ReadsWithinTheLimit_PassThrough()
    {
        using var stream = new ReadLimitStream(new MemoryStream([1, 2, 3]), TimeSpan.FromSeconds(5), () => throw new InvalidOperationException("not expected"));
        var buffer = new byte[10];

        Assert.Equal(3, await stream.ReadAsync(buffer, 0, 10, Ct));
        Assert.Equal(0, await stream.ReadAsync(buffer, 0, 10, Ct));
    }

    [Fact]
    public async Task BrokenConnection_IsNotKeptInThePool()
    {
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);

        using (var lease = await pool.AcquireAsync(Ct))
        {
            created[0].IsBroken = true;
            lease.Keep();
        }

        using (await pool.AcquireAsync(Ct))
        {
        }

        Assert.Equal(2, created.Count);
        Assert.True(created[0].Disposed);
    }

    // A read that completes only when the stream is disposed, ignoring the token, as an
    // asynchronous socket read on .NET Framework.
    private sealed class NeverEndingStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _read.Task;

#if NET
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_read.Task);
#endif

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _read.TrySetException(new ObjectDisposedException(nameof(NeverEndingStream)));
            base.Dispose(disposing);
        }
    }

    private sealed class StubConnection : IDatasusConnection
    {
        public bool IsBroken { get; set; }

        public bool Disposed { get; private set; }

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;
    }

    private sealed class StubFactory(List<StubConnection> created) : IDatasusConnectionFactory
    {
        public Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            var connection = new StubConnection();
            created.Add(connection);
            return Task.FromResult<IDatasusConnection>(connection);
        }
    }
}
