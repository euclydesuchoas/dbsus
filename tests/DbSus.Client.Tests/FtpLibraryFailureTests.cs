using System.Collections.Concurrent;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The real FTP transport (FluentFTP) against a loopback server that closes the control connection
// without a word. FluentFTP 55.0.0 can then fail inside a listing with an exception of its own
// making (a NullReferenceException) instead of a network error. Whatever FluentFTP does, the
// caller must get the listing, from a new connection, or the client's own exception.
public sealed class FtpLibraryFailureTests : IDisposable
{
    private readonly LoopbackFtpServer _server = new() { Listing = [LoopbackFtpServer.FileLine("A.dbc", 10)] };
    private readonly ConcurrentQueue<DatasusRetryInfo> _retries = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task IdleConnectionClosedSilently_TheNextListingComesFromANewConnection()
    {
        _server.CloseControlConnectionSilentlyAfter = "MDTM";
        using var client = Client(maxRetries: 1);

        // The connection goes back to the pool, and the server closes it there.
        await client.GetInfoAsync("/X/A.dbc", Ct);
        await _server.ControlConnectionClosedSilently;

        var entries = await client.ListAsync("/X", Ct);

        Assert.Equal(["A.dbc"], entries.Select(entry => entry.Name));
        Assert.Equal(2, _server.ControlConnections);
        Assert.True(_retries.Count <= 1, $"{_retries.Count} retries");
    }

    [Fact]
    public async Task ConnectionClosedSilentlyRightAfterLogin_TheListingComesFromANewConnection()
    {
        _server.CloseControlConnectionSilentlyAfter = "TYPE";
        using var client = Client(maxRetries: 1);

        var entries = await client.ListAsync("/X", Ct);

        Assert.Equal(["A.dbc"], entries.Select(entry => entry.Name));
        Assert.Equal(2, _server.ControlConnections);
        Assert.True(_retries.Count <= 1, $"{_retries.Count} retries");
    }

    [Theory]
    [InlineData("MDTM")]
    [InlineData("TYPE")]
    public async Task ConnectionClosedSilently_WithoutRetries_FailsOnlyWithTheClientsOwnException(string closeAfter)
    {
        _server.CloseControlConnectionSilentlyAfter = closeAfter;
        using var client = Client(maxRetries: 0);
        if (closeAfter == "MDTM")
        {
            await client.GetInfoAsync("/X/A.dbc", Ct);
            await _server.ControlConnectionClosedSilently;
        }

        // FluentFTP may reopen the connection by itself, and then the listing succeeds.
        var exception = await Record.ExceptionAsync(() => client.ListAsync("/X", Ct));

        if (exception is not null)
        {
            var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(exception);
            Assert.Equal(1, exhausted.Attempts);
            Assert.StartsWith($"Listing /X failed after 1 attempt: FTP transport failure while listing /dissemin/publicos/X on 127.0.0.1:{_server.Port}: ", exhausted.Message, StringComparison.Ordinal);
            Assert.NotNull(exhausted.InnerException);
            if (exhausted.Message.Contains("unexpected", StringComparison.Ordinal))
            {
                // The library's own exception, as it was thrown.
                Assert.Contains($"an unexpected {exhausted.InnerException.GetType().Name}", exhausted.Message, StringComparison.Ordinal);
                Assert.Contains("FluentFTP", exhausted.InnerException.StackTrace, StringComparison.Ordinal);
            }
        }
    }

    private DatasusClient Client(int maxRetries) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxConnections = 1,
        MaxRetries = maxRetries,
        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReadTimeout = TimeSpan.FromSeconds(5),
        OnRetry = _retries.Enqueue,
    });
}
