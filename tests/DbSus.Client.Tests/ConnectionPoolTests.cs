using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// The pool's places: a lease frees its place once, whatever happens to its connection.
public sealed class ConnectionPoolTests
{
    [Fact(Timeout = 10_000)]
    public async Task ConnectionWhoseDisposeThrows_StillFreesItsPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created, () => new StubConnection { DisposeThrows = created.Count == 0 }), maxConnections: 1);
        var lease = await pool.AcquireAsync(ct);

        Assert.Throws<InvalidOperationException>(lease.Dispose);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using (await pool.AcquireAsync(timeout.Token))
        {
        }

        Assert.Equal(2, created.Count);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplaceConnection_KeepsThePlace_AndClosesTheOldConnectionOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);
        var lease = await pool.AcquireAsync(ct);
        var waiting = pool.AcquireAsync(ct);

        await lease.ReplaceConnectionAsync(ct);

        Assert.Equal(2, created.Count);
        Assert.Same(created[1], lease.Connection);
        Assert.Equal(1, created[0].Disposals);
        await Task.Delay(100, ct);
        Assert.False(waiting.IsCompleted);

        lease.Dispose();

        using (await waiting)
        {
        }

        Assert.Equal(1, created[0].Disposals);
        Assert.Equal(1, created[1].Disposals);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplacedConnection_KeptAfterACleanOperation_GoesBackToThePool()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);

        using (var lease = await pool.AcquireAsync(ct))
        {
            lease.Keep();
            await lease.ReplaceConnectionAsync(ct);
            lease.Keep();
        }

        using (var again = await pool.AcquireAsync(ct))
        {
            Assert.Same(created[1], again.Connection);
        }

        Assert.Equal(2, created.Count);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplaceConnection_KeepFromBefore_DoesNotKeepTheNewConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);

        using (var lease = await pool.AcquireAsync(ct))
        {
            lease.Keep();
            await lease.ReplaceConnectionAsync(ct);
        }

        Assert.Equal(1, created[1].Disposals);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplaceConnection_ConnectingFails_DisposeFreesThePlaceOnceAndClosesNothingTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        var calls = 0;
        using var pool = new ConnectionPool(
            new StubFactory(created, () => ++calls == 2 ? throw new TransientTransportException("Connection refused.") : new StubConnection()),
            maxConnections: 1);
        var lease = await pool.AcquireAsync(ct);

        await Assert.ThrowsAsync<TransientTransportException>(() => lease.ReplaceConnectionAsync(ct));

        Assert.Throws<InvalidOperationException>(() => lease.Connection);
        lease.Abort();
        lease.Keep();
        lease.Dispose();
        lease.Dispose();
        Assert.Equal(1, created[0].Disposals);

        // The place was freed exactly once: one lease comes at once, a second one waits for it.
        using var first = await pool.AcquireAsync(ct);
        var second = pool.AcquireAsync(ct);
        await Task.Delay(100, ct);
        Assert.False(second.IsCompleted);
        first.Dispose();
        (await second).Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplaceConnection_PoolDisposed_ThrowsObjectDisposedWithoutConnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);
        var lease = await pool.AcquireAsync(ct);

        pool.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReplaceConnectionAsync(ct));
        Assert.Single(created);
        Assert.Equal(1, created[0].Disposals);
        lease.Dispose();
        Assert.Equal(1, created[0].Disposals);
    }

    [Fact(Timeout = 10_000)]
    public async Task ReplaceConnection_AfterDispose_ThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);
        var lease = await pool.AcquireAsync(ct);
        lease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => lease.ReplaceConnectionAsync(ct));

        Assert.Single(created);
    }

    [Fact(Timeout = 10_000)]
    public async Task Abort_ClosesAnAbortableConnectionByForce()
    {
        var ct = TestContext.Current.CancellationToken;
        var created = new List<StubConnection>();
        using var pool = new ConnectionPool(new StubFactory(created), maxConnections: 1);

        using (var lease = await pool.AcquireAsync(ct))
        {
            lease.Abort();
            lease.Keep();
        }

        Assert.Equal(1, created[0].Aborts);
        Assert.Equal(1, created[0].Disposals);
    }

    [Fact]
    public void RemoteFileChanged_IsATransferFailureWithBothVersions()
    {
        var expected = new RemoteFileInfo(100, new DateTime(2025, 8, 8, 12, 0, 0, DateTimeKind.Utc));
        var current = new RemoteFileInfo(120, new DateTime(2025, 8, 9, 12, 0, 0, DateTimeKind.Utc));

        DatasusTransferException exception = new DatasusRemoteFileChangedException("changed", RemotePath.Parse("/X/A.dbc", "/dissemin/publicos"), expected, current);

        var changed = Assert.IsType<DatasusRemoteFileChangedException>(exception);
        Assert.Equal("/X/A.dbc", changed.RemotePath);
        Assert.Equal(("/X/A.dbc", "A.dbc", 100L, (DateTime?)expected.LastModifiedUtc, false), (changed.Expected.Path, changed.Expected.Name, changed.Expected.Size, changed.Expected.LastModifiedUtc, changed.Expected.IsDirectory));
        Assert.Equal(("/X/A.dbc", "A.dbc", 120L, (DateTime?)current.LastModifiedUtc, false), (changed.Current.Path, changed.Current.Name, changed.Current.Size, changed.Current.LastModifiedUtc, changed.Current.IsDirectory));
        Assert.Null(changed.FtpReplyCode);
        Assert.Null(changed.InnerException);
    }

    internal sealed class StubConnection : IDatasusConnection, IAbortableConnection
    {
        public bool IsBroken { get; set; }

        public bool DisposeThrows { get; set; }

        public int Disposals { get; private set; }

        public int Aborts { get; private set; }

        public void Abort()
        {
            Aborts++;
            IsBroken = true;
        }

        public Task<IReadOnlyList<RemoteEntry>> ListAsync(string directory, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteFileInfo> GetInfoAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
            Disposals++;
            if (DisposeThrows)
            {
                throw new InvalidOperationException("The stub connection fails to close.");
            }
        }
    }

    internal sealed class StubFactory(List<StubConnection> created, Func<StubConnection>? create = null) : IDatasusConnectionFactory
    {
        public Task<IDatasusConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var connection = create?.Invoke() ?? new StubConnection();
            created.Add(connection);
            return Task.FromResult<IDatasusConnection>(connection);
        }
    }
}
