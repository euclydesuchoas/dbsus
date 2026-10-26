using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamResumeTests;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// The read-ahead stream resumes with REST. Only after a resumption of the same sequence failed to
// open does it start over from byte 0, without REST: when it received at most the restart size, or
// on the last attempt of the budget; otherwise it tries REST again. A failure in the middle of a
// transfer that opened is no such evidence, and the evidence ends with the sequence, when enough
// arrives to start the count of attempts again. Starting over, the pump reads again the bytes it
// already delivered and drops them, unpublished; they are no progress.
public sealed class ReadAheadStreamRestartTests
{
    private const string FilePath = "/X/A.dbc";
    private static readonly RemotePath Remote = RemotePath.Parse(FilePath, FakeServer.Root);

    [Fact(Timeout = 30_000)]
    public async Task DropsEveryFewBytes_BelowTheRestartSize_CompleteWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        const int Step = 1_200_000;
        var content = Content(3_300_000, seed: 201);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(Step);
        server.FailNextTransferAfter(Step);
        var retries = new Retries(1);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(202));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, Step, 2 * Step }, server.TransferOffsets);
        Assert.Equal(content.Length, server.BytesServed);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task FiveConnections_FourRetries_CompleteOnTheFifthWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(900_000, seed: 203);
        var server = new FakeServer();
        server.Add(FilePath, content);
        for (var i = 0; i < 4; i++)
        {
            server.FailNextTransferAfter(200_000);
        }

        var retries = new Retries(4);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(204));

        // The fifth transfer is the last attempt of the budget, and it still resumes: no resumption
        // failed to open.
        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 200_000, 400_000, 600_000, 800_000 }, server.TransferOffsets);
        Assert.Equal([1, 2, 3, 4], retries.Infos.Select(info => info.Attempt));
        Assert.Equal(content.Length, server.BytesServed);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task FailuresInTheMiddleOfTransfersThatOpened_AreNoEvidence_EvenOnTheLastAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(900_000, seed: 205);
        var server = new FakeServer();
        server.Add(FilePath, content);
        for (var i = 0; i < 3; i++)
        {
            server.FailNextTransferAfter(100_000);
        }

        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(206));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 100_000, 200_000, 300_000 }, server.TransferOffsets);
        Assert.Equal(content.Length, server.BytesServed);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(1)]
    [InlineData(600_123)]
    [InlineData(699_999)]
    public async Task RestUnansweredOnce_SmallReceived_StartsOverFromByteZero_AndDropsExactlyWhatWasDelivered(int stoppedAt)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(700_000, seed: 207);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);
        server.FailNextTransferAfter(stoppedAt);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(208), maxChunk: 9_000);

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, stoppedAt, 0 }, server.TransferOffsets);
        Assert.Equal(stoppedAt + content.Length, server.BytesServed);
        Assert.Equal([1, 2], retries.Infos.Select(info => info.Attempt));
        Assert.True(Assert.IsType<TransientTransportException>(retries.Infos[1].Exception).IsExpiredLimit);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task RestAlwaysUnanswered_LargeReceived_TriesRestAgain_ThenTheLastAttemptStartsOver()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(900_000, seed: 209);
        var server = new FakeServer { ResumptionsThatTimeOut = int.MaxValue };
        server.Add(FilePath, content);
        server.FailNextTransferAfter(600_000);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);

        // A restart size below what arrived: with the evidence, REST again until the last attempt.
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), restartFromStartUpTo: 100_000, ct);

        var (data, failure) = ReadAll(stream, new Random(210));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 600_000, 600_000, 0 }, server.TransferOffsets);
        Assert.Equal([1, 2, 3], retries.Infos.Select(info => info.Attempt));
        Assert.All(retries.Infos.Skip(1), info => Assert.True(Assert.IsType<TransientTransportException>(info.Exception).IsExpiredLimit));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task TheEvidenceEndsWithTheSequence_TheNextFailureResumesWithRestAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3_000_000, seed: 211);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);

        // The first transfer drops at 200,000; the resumption fails to open; the transfer from byte 0
        // drops at 1,500,000, more than ResetAfterBytes past the last failure: a new sequence, without
        // the evidence, so the next failure resumes with REST.
        server.FailNextTransferAfter(200_000);
        server.FailNextTransferAfter(1_500_000);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(212));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 200_000, 0, 1_500_000 }, server.TransferOffsets);
        Assert.Equal([1, 2, 1], retries.Infos.Select(info => info.Attempt));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    // The evidence of the resumption that failed to open is spent once the transfer that started
    // over has read again the bytes already delivered: a failure after them resumes with REST.
    [Fact(Timeout = 30_000)]
    public async Task DropAfterTheTransferThatStartedOverCaughtUp_TheNextAttemptResumesWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(20_000, seed: 217);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);
        server.FailNextTransferAfter(3000);
        server.FailNextTransferAfter(5000);
        var retries = new Retries(6);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(218));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 3000, 0, 5000 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task BytesReadAgainAndDropped_AreNoProgress_ForTheCountOfAttempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3 * RemoteReadAheadStream.Capacity, seed: 213);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);

        // The transfer from byte 0 reads again more than ResetAfterBytes before it fails, all of it
        // already delivered: the count does not start again, and the budget runs out.
        server.FailNextTransferAfter(2_500_000);
        server.FailNextTransferAfter(2_200_000);
        var retries = new Retries(2);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(214));

        AssertSameBytes(content.Take(2_500_000).ToArray(), data);
        Assert.Equal(3, Assert.IsType<DatasusRetriesExhaustedException>(failure).Attempts);
        Assert.Equal([1, 2], retries.Infos.Select(info => info.Attempt));
        Assert.Equal(new long[] { 0, 2_500_000, 0 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task TransferThatEndsWhileDropping_EndedEarly_AndStartsOverAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(700_000, seed: 215);
        var server = new ScriptedTransferServer(content) { ResumptionsThatFail = 1 };
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, 300_000));
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.EndEarly, 100_000));
        var retries = new Retries(4);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(216));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 300_000, 0, 0 }, server.TransferOffsets);
        Assert.Equal([1, 2, 3], retries.Infos.Select(info => info.Attempt));
        Assert.Contains("Received 100000 of 700000 bytes", retries.Infos[2].Exception.Message, StringComparison.Ordinal);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task VersionChangedBeforeStartingOver_FailsAfterTheBytesDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(800_000, seed: 217);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);
        server.FailNextTransferAfter(400_000);
        var waits = 0;
        var retries = new Retries(3, (_, _) =>
        {
            if (++waits == 2)
            {
                server.Add(FilePath, Content(800_000, seed: 218), FakeServer.Time(5));
            }

            return Task.CompletedTask;
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(219));

        AssertSameBytes(content.Take(400_000).ToArray(), data);
        Assert.Equal(FakeServer.Time(5), Assert.IsType<DatasusRemoteFileChangedException>(failure).Current.LastModifiedUtc);
        Assert.Equal(new long[] { 0, 400_000 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerSendsMoreAfterStartingOver_FailsAfterEveryByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(500_000, seed: 220);
        var server = new FakeServer { ResumptionsThatTimeOut = 1 };
        server.Add(FilePath, content);
        server.FailNextTransferAfter(200_000);
        server.SendExtraBytes(2, 10);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(221));

        // The check one byte beyond the size, after the drop, sees the excess as after any opening.
        AssertSameBytes(content, data);
        Assert.Contains("sent more than", Assert.IsType<DatasusRetriesExhaustedException>(failure).Message, StringComparison.Ordinal);
        Assert.Equal(new long[] { 0, 200_000, 0 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_RestUnansweredOnce_TheNextAttemptSendsRetrWithoutRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(400_000, seed: 222);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;
        server.StallTransferAfter = 100_000;
        var waits = 0;
        var retries = new Retries(3, (_, _) =>
        {
            // After the stall, the REST goes unanswered once; after that, the server answers again.
            server.StallTransferAfter = null;
            server.SilentOn = ++waits == 1 ? "REST" : null;
            return Task.CompletedTask;
        });
        using var pool = LoopbackPool(server, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(300));
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(223));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(1, server.Commands.Count(command => command == "REST"));
        Assert.Equal(2, server.Commands.Count(command => command == "RETR"));
        Assert.True(Assert.IsType<TransientTransportException>(retries.Infos[1].Exception).IsExpiredLimit);
        await AssertEndsAsync(stream);
    }
}
