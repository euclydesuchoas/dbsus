using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A listing counts only when the server confirms it (226 after the data connection closes).
// FluentFTP 55 returns what it read, as a success, when the data connection fails while the
// control connection stays open, or when the final reply never comes.
public sealed class ListingCompletionTests : IDisposable
{
    private readonly LoopbackFtpServer _server = new()
    {
        AdvertiseEpsv = false,
        Listing = [LoopbackFtpServer.FileLine("PAMG2407a.dbc", 10), LoopbackFtpServer.FileLine("PAMG2407b.dbc", 10)],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ConfirmedListing_IsReturned()
    {
        using var client = Client();

        var entries = await client.ListAsync("/X", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc"], entries.Select(entry => entry.Name));
    }

    [Fact(Timeout = 30_000)]
    public Task DataConnectionClosedWithoutFinalReply_FailsAndIsNotCached() => AssertNotUsed(ListingEnd.CloseWithoutReply, TestContext.Current.CancellationToken);

    [Fact(Timeout = 30_000)]
    public Task DataConnectionResetWithoutFinalReply_FailsAndIsNotCached() => AssertNotUsed(ListingEnd.ResetWithoutReply, TestContext.Current.CancellationToken);

    [Fact]
    public Task DataConnectionResetThenAborted_FailsAndIsNotCached() => AssertNotUsed(ListingEnd.ResetThenAbort, Ct);

    private async Task AssertNotUsed(ListingEnd end, CancellationToken ct)
    {
        _server.ListingEnd = end;
        using var client = Client();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", ct));

        // The first listing failed at its end, after its LIST: a failure before it, connecting or
        // logging in, would not be the case under test.
        Assert.Equal(1, _server.Commands.Count(command => command == "LIST"));
        _server.ListingEnd = ListingEnd.Complete;
        var entries = await client.ListAsync("/X", ct);

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // Timeouts that a slow runner does not reach while connecting and logging in; the read timeout
    // is also how long the client waits for the final reply that never comes.
    private DatasusClient Client() => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = 0,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ReadTimeout = TimeSpan.FromSeconds(5),
    });
}
