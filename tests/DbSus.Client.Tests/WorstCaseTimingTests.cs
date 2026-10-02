using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The worst-case times given in the README, checked with small timeouts against a loopback server
// whose data connection opens and then stays silent. With MaxRetries = 4 there are 5 attempts;
// the waits between them are nominally 1, 2, 4 and 8 times InitialRetryDelay, each between half
// and all of it, so 7.5 to 15 times InitialRetryDelay in all. They run alone (no other test in
// parallel) so that a busy machine does not stretch them; the lower bound checks the formula, the
// upper one leaves room for a slow runner.
[Collection(nameof(RunAlone))]
[Trait("Category", "Timing")]
public sealed class WorstCaseTimingTests : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    // Connecting and logging in on every attempt, and a slow or busy machine.
    private static readonly TimeSpan Overhead = TimeSpan.FromSeconds(3);
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    // A listing waits ReadTimeout for data, then ReadTimeout for the server's final reply.
    [Fact(Timeout = 60_000)]
    public async Task StalledListing_TakesTwoReadTimeoutsPerAttemptPlusTheWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Listing = [];
        _server.ListingEnd = ListingEnd.Stall;
        using var client = Client();
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", ct));

        AssertWithin(watch.Elapsed, perAttempt: TimeSpan.FromTicks(2 * ReadTimeout.Ticks));
    }

    // A download's read is cut by the client after ReadTimeout, which closes the connection: there
    // is no second wait for a final reply.
    [Fact(Timeout = 60_000)]
    public async Task StalledDownload_TakesOneReadTimeoutPerAttemptPlusTheWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files["/dissemin/publicos/X/A.dbf"] = new byte[1000];
        _server.StallTransferAfter = 0;
        using var client = Client();
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.DownloadAsync("/X/A.dbf", ct));

        AssertWithin(watch.Elapsed, perAttempt: ReadTimeout);
    }

    private static void AssertWithin(TimeSpan elapsed, TimeSpan perAttempt)
    {
        var lowest = TimeSpan.FromTicks((perAttempt.Ticks * 5) + (RetryDelay.Ticks * 15 / 2));
        var highest = TimeSpan.FromTicks((perAttempt.Ticks * 5) + (RetryDelay.Ticks * 15));
        TestContext.Current.SendDiagnosticMessage($"{TestContext.Current.Test?.TestDisplayName}: {elapsed.TotalMilliseconds:0} ms (formula {lowest.TotalMilliseconds:0} to {highest.TotalMilliseconds:0} ms)");
        Assert.True(
            elapsed >= lowest - TimeSpan.FromMilliseconds(100) && elapsed <= highest + Overhead,
            $"Took {elapsed.TotalMilliseconds:0} ms; the formula gives {lowest.TotalMilliseconds:0} to {highest.TotalMilliseconds:0} ms.");
    }

    private DatasusClient Client() => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = 4,
        InitialRetryDelay = RetryDelay,
        ConnectTimeout = TimeSpan.FromMilliseconds(200),
        ReadTimeout = ReadTimeout,
    });
}

// Tests in this collection run with no other test in parallel.
[CollectionDefinition(nameof(RunAlone), DisableParallelization = true)]
public sealed class RunAlone;
