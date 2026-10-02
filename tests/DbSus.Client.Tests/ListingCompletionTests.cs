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

    [Fact]
    public Task DataConnectionClosedWithoutFinalReply_FailsAndIsNotCached()
    {
        SkipWhereReadsIgnoreTheTimeout();
        return AssertNotUsed(ListingEnd.CloseWithoutReply);
    }

    [Fact]
    public Task DataConnectionResetWithoutFinalReply_FailsAndIsNotCached()
    {
        SkipWhereReadsIgnoreTheTimeout();
        return AssertNotUsed(ListingEnd.ResetWithoutReply);
    }

    [Fact]
    public Task DataConnectionResetThenAborted_FailsAndIsNotCached() => AssertNotUsed(ListingEnd.ResetThenAbort);

    private async Task AssertNotUsed(ListingEnd end)
    {
        _server.ListingEnd = end;
        using var client = Client();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));
        _server.ListingEnd = ListingEnd.Complete;
        var entries = await client.ListAsync("/X", Ct);

        Assert.Equal(2, entries.Count);
        Assert.Equal(2, _server.Commands.Count(command => command == "LIST"));
    }

    // FluentFTP 55 enforces ReadTimeout on asynchronous reads with a cancellation token, which
    // NetworkStream.ReadAsync on .NET Framework does not observe once the read has started: there,
    // waiting for a final reply that never comes blocks.
    private static void SkipWhereReadsIgnoreTheTimeout()
    {
#if NETFRAMEWORK
        Assert.Skip("On .NET Framework, FluentFTP's asynchronous reads ignore ReadTimeout, so a missing final reply blocks.");
#endif
    }

    private DatasusClient Client() => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = 0,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = TimeSpan.FromSeconds(1),
    });
}
