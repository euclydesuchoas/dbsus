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

    internal sealed class StubConnection : IDatasusConnection
    {
        public bool IsBroken { get; set; }

        public bool DisposeThrows { get; set; }

        public int Disposals { get; private set; }

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
