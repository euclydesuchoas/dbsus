using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using DbSus.Tests.TestSupport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// The read-ahead stream resumes a transfer that drops, stalls or ends early: on a new connection in
// the same place, on the same version, from the end of what the pump received, with the client's
// backoff and OnRetry, counting the attempts in a row without progress. Every test ends with the
// pump over, its place free and no connection left open.
public sealed class ReadAheadStreamResumeTests
{
    private const string FilePath = "/X/A.dbc";
    private const int Capacity = RemoteReadAheadStream.Capacity;
    private static readonly RemotePath Remote = RemotePath.Parse(FilePath, FakeServer.Root);
    private static readonly int[] FirstTwoAttempts = [1, 2];
    private static readonly int[] FirstThreeAttempts = [1, 2, 3];

    [Fact(Timeout = 30_000)]
    public async Task Drop_SlowConsumer_ResumesFromWhatThePumpReceived()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(900_000, seed: 51);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(600_000);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        // Nothing is read until the pump is over: what it received is more than what was read.
        await AssertEndsAsync(stream);
        Assert.Equal(new long[] { 0, 600_000 }, server.TransferOffsets);

        var (data, failure) = ReadAll(stream, new Random(52));
        Assert.Null(failure);
        AssertSameBytes(content, data);
        var retry = Assert.Single(retries.Infos);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal("Streaming " + FilePath, retry.Operation);
        Assert.Equal(2, server.ConnectionsOpened);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task Drop_BufferFull_ResumesFromTheEndOfTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((3 * Capacity) + 5, seed: 53);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(Capacity + 1000);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);
        await WaitUntilAsync(() => stream.MaxBuffered == Capacity, "the pump to fill the buffer");

        var data = new MemoryStream();
        data.Write(ReadExactly(stream, 300_000), 0, 300_000);
        var (rest, failure) = ReadAll(stream, new Random(54));
        data.Write(rest, 0, rest.Length);

        Assert.Null(failure);
        AssertSameBytes(content, data.ToArray());
        Assert.Equal(new long[] { 0, Capacity + 1000 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task DropsWithProgressBetweenThem_MoreThanMaxRetries_DoNotRunOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((7 * Capacity) + 3, seed: 55);
        var server = new FakeServer();
        server.Add(FilePath, content);
        const int Step = RemoteReadAheadStream.ResetAfterBytes + 100_000;
        for (var i = 0; i < 5; i++)
        {
            server.FailNextTransferAfter(Step);
        }

        var retries = new Retries(2);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(56));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(Enumerable.Range(0, 6).Select(i => (long)i * Step), server.TransferOffsets);
        Assert.Equal(5, retries.Infos.Count);
        Assert.All(retries.Infos, info => Assert.Equal(1, info.Attempt));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task DropsInARowWithoutProgress_RunOut_AfterTheBytesBeforeThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(500_000, seed: 57);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(200_000);
        for (var i = 0; i < 3; i++)
        {
            server.FailNextTransferAfter(1000);
        }

        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(58));

        AssertSameBytes(content.Take(203_000).ToArray(), data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.Equal(4, exhausted.Attempts);
        Assert.Equal(FilePath, exhausted.RemotePath);
        Assert.Contains("failed after 4 attempts", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal(FirstThreeAttempts, retries.Infos.Select(info => info.Attempt));

        // The client's backoff: from 100 ms, doubled each time, each wait between half and all of it.
        for (var i = 0; i < 3; i++)
        {
            var nominal = TimeSpan.FromMilliseconds(100 << i);
            Assert.InRange(retries.Infos[i].Delay, TimeSpan.FromTicks(nominal.Ticks / 2), nominal);
        }

        Assert.Equal(4, server.Transfers);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(nameof(ScriptedFaultKind.EndEarly))]
    [InlineData(nameof(ScriptedFaultKind.Drop))]
    [InlineData(nameof(ScriptedFaultKind.Stall))]
    [InlineData(nameof(ScriptedFaultKind.StallIgnoringToken))]
    public async Task FailureOnce_IsResumed(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var kind = fault switch
        {
            nameof(ScriptedFaultKind.EndEarly) => ScriptedFaultKind.EndEarly,
            nameof(ScriptedFaultKind.Drop) => ScriptedFaultKind.Drop,
            nameof(ScriptedFaultKind.Stall) => ScriptedFaultKind.Stall,
            _ => ScriptedFaultKind.StallIgnoringToken,
        };
        var content = Content(700_000, seed: 59);
        var server = new ScriptedTransferServer(content) { ReadTimeout = TimeSpan.FromMilliseconds(200) };
        server.Faults.Enqueue(new ScriptedFault(kind, 300_000));
        var retries = new Retries(2);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(60));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, 300_000 }, server.TransferOffsets);
        Assert.Equal(1, server.InfoCalls);
        Assert.Equal(1, server.Aborted);
        var retry = Assert.Single(retries.Infos);
        if (kind is ScriptedFaultKind.Stall or ScriptedFaultKind.StallIgnoringToken)
        {
            Assert.IsType<TimeoutException>(retry.Exception.InnerException);
        }

        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task VersionChangedBeforeTheResumption_FailsAfterTheBytesOfTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(800_000, seed: 61);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(400_000);

        // The file is republished while the stream waits to resume.
        var retries = new Retries(3, (_, _) =>
        {
            server.Add(FilePath, Content(800_000, seed: 62), FakeServer.Time(5));
            return Task.CompletedTask;
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(63));

        AssertSameBytes(content.Take(400_000).ToArray(), data);
        var changed = Assert.IsType<DatasusRemoteFileChangedException>(failure);
        Assert.Equal(FakeServer.Time(0), changed.Expected.LastModifiedUtc);
        Assert.Equal(FakeServer.Time(5), changed.Current.LastModifiedUtc);
        Assert.Equal(FilePath, changed.RemotePath);
        Assert.Equal(2, changed.Attempts);
        Assert.Equal(1, server.Transfers);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task StoppedDuringTheBackoffOrTheReconnection_EndsAtOnce(bool duringBackoff, bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 64);
        var server = new ScriptedTransferServer(content);
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, 1000));
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!duringBackoff)
        {
            server.BeforeConnect = async (number, token) =>
            {
                if (number == 2)
                {
                    waiting.TrySetResult(true);
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
            };
        }

        var retries = new Retries(3, async (_, token) =>
        {
            if (duringBackoff)
            {
                waiting.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, cancellation.Token);
        await WithinAsync(waiting.Task, ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (cancel)
        {
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(ReadAll(stream, new Random(65)).Failure);
        }
        else
        {
            stream.Dispose();
        }

        await AssertEndsAsync(stream);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), watch.Elapsed.ToString());
        Assert.Equal(duringBackoff ? 1 : 2, server.Opened);
        stream.Dispose();
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task PoolDisposed_ThenADrop_FailsWithObjectDisposedAfterTheBytesOfTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(600_000, seed: 66);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(300_000);
        var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, new Retries(3).Policy, Remote, Version(content.Length), ct);

        pool.Dispose();

        var (data, failure) = ReadAll(stream, new Random(67));
        AssertSameBytes(content.Take(300_000).ToArray(), data);
        var disposed = Assert.IsType<ObjectDisposedException>(failure);
        Assert.Contains("could not resume at byte 300000", disposed.Message, StringComparison.Ordinal);
        Assert.Equal(1, server.ConnectionsOpened);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task DuringTheBackoff_TheConsumerReadsWhatTheBufferHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(600_000, seed: 68);
        var server = new ScriptedTransferServer(content);
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, 500_000));
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retries = new Retries(3, async (_, token) =>
        {
            waiting.TrySetResult(true);
            await UntilAsync(release.Task, token).ConfigureAwait(false);
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);
        await WithinAsync(waiting.Task, ct);

        var first = ReadExactly(stream, 500_000);

        Assert.False(release.Task.IsCompleted);
        Assert.Equal(1, server.Transfers);
        release.SetResult(true);
        var (rest, failure) = ReadAll(stream, new Random(69));
        Assert.Null(failure);
        AssertSameBytes(content, [.. first, .. rest]);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileGoneWhenResuming_IsTheEnd(bool goneBeforeSize)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(600_000, seed: 70);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(300_000);
        if (!goneBeforeSize)
        {
            // SIZE and MDTM still find it; RETR does not (550).
            server.BeforeTransfer = transfer =>
            {
                if (transfer == 2)
                {
                    server.Remove(FilePath);
                }
            };
        }

        var retries = new Retries(3, (_, _) =>
        {
            if (goneBeforeSize)
            {
                server.Remove(FilePath);
            }

            return Task.CompletedTask;
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(71));

        AssertSameBytes(content.Take(300_000).ToArray(), data);
        Assert.IsType<FileNotFoundException>(failure);
        Assert.Single(retries.Infos);
        Assert.Equal(goneBeforeSize ? 1 : 2, server.Transfers);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConnectionAndSizeFailWhileResuming_AreRetriedInTheSameCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(600_000, seed: 72);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(300_000);
        var waits = 0;
        var retries = new Retries(3, (_, _) =>
        {
            // The first wait is followed by a refused connection, the second by a failing SIZE.
            switch (++waits)
            {
                case 1:
                    server.FailNextConnects(1, () => new TransientTransportException("Connection refused."));
                    break;
                case 2:
                    server.FailNextOperations(1, () => new TransientTransportException("421 Service not available.", "421"));
                    break;
            }

            return Task.CompletedTask;
        });
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(73));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(FirstThreeAttempts, retries.Infos.Select(info => info.Attempt));
        Assert.Equal(new long[] { 0, 300_000 }, server.TransferOffsets);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task OpeningFailsTransiently_IsRetriedByThePolicy()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(10_000, seed: 74);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextOperations(2, () => new TransientTransportException("421 Too many connections.", "421"));
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        AssertSameBytes(content, ReadAll(stream, new Random(75)).Data);
        Assert.Equal(FirstTwoAttempts, retries.Infos.Select(info => info.Attempt));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("EQRR0509.dbc")]
    [InlineData("LTRR0610.dbc")]
    public async Task CommittedFixture_DropInTheCompressedData_SameRecordsAndChecksum(string file)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(RepositoryPaths.Fixtures, file);
        var dbc = File.ReadAllBytes(path);
        var compressedStart = (dbc[8] | (dbc[9] << 8)) + 4;
        var server = new FakeServer();
        server.Add(FilePath, dbc);
        server.FailNextTransferAfter(compressedStart + ((dbc.Length - compressedStart) / 2));
        var retries = new Retries(2);
        using var pool = new ConnectionPool(server.Factory(), 1);
        var mismatches = 0;
        var options = new DbSus.Dbc.DbcReadOptions { ChecksumMismatch = _ => mismatches++ };

        var expected = Records(DbSusFile.OpenDbcReader(path));
        var actual = Records(DbSusFile.OpenDbcReader(await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(dbc.Length), ct), dbcOptions: options));

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"{file}: record {i + 1} differs.");
        }

        Assert.Equal(0, mismatches);
        Assert.Single(retries.Infos);
        Assert.Equal(2, server.Transfers);
        await AssertNothingHeldAsync(pool, () => server.ActiveConnections);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_StalledTransfers_ResumeWithRestUntilTheEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((3 * Capacity) + 77, seed: 76);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;

        // Every transfer stalls after this many bytes from where it started, so each resumption
        // progresses. The last one reaches the end of the file and stalls there too, keeping the
        // data connection open: the stream ends at the read timeout, without another resumption.
        const int Step = RemoteReadAheadStream.ResetAfterBytes + 200_000;
        server.StallTransferAfter = Step;
        var retries = new Retries(1);
        using var pool = LoopbackPool(server, TimeSpan.FromMilliseconds(500));
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(77));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(3, server.Commands.Count(command => command == "RETR"));
        Assert.Equal(new long[] { Step, 2 * Step }, server.Restarts);
        Assert.Equal(2, retries.Infos.Count);
        await AssertEndsAsync(stream);
        await AssertDiscardedAsync(pool, () => server.ControlConnections, () => 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_VersionChangedBeforeTheResumption_FailsAfterTheBytesOfTheBuffer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(500_000, seed: 78);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;
        server.StallTransferAfter = 200_000;
        var retries = new Retries(3, (_, _) =>
        {
            server.StallTransferAfter = null;
            server.Files[FakeServer.Root + FilePath] = Content(500_001, seed: 79);
            return Task.CompletedTask;
        });
        using var pool = LoopbackPool(server, TimeSpan.FromMilliseconds(500));
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(80));

        AssertSameBytes(content.Take(200_000).ToArray(), data);
        var changed = Assert.IsType<DatasusRemoteFileChangedException>(failure);
        Assert.Equal(500_001, changed.Current.Size);
        Assert.Equal(1, server.Commands.Count(command => command == "RETR"));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => 0);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(nameof(ScriptedFaultKind.Drop))]
    [InlineData(nameof(ScriptedFaultKind.Stall))]
    [InlineData(nameof(ScriptedFaultKind.StallIgnoringToken))]
    public async Task FailureAfterTheLastByte_EndsWithoutResuming_AndDiscardsTheConnection(string fault)
    {
        var ct = TestContext.Current.CancellationToken;
        var kind = fault switch
        {
            nameof(ScriptedFaultKind.Drop) => ScriptedFaultKind.Drop,
            nameof(ScriptedFaultKind.Stall) => ScriptedFaultKind.Stall,
            _ => ScriptedFaultKind.StallIgnoringToken,
        };
        var content = Content(300_000, seed: 81);
        var server = new ScriptedTransferServer(content) { ReadTimeout = TimeSpan.FromMilliseconds(200) };

        // The transfer fails where the pump reads one byte beyond the size.
        server.Faults.Enqueue(new ScriptedFault(kind, content.Length));
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(82));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
        await AssertEndsAsync(stream);
        Assert.Equal(new long[] { 0 }, server.TransferOffsets);
        Assert.Empty(retries.Infos);
        Assert.Equal(0, server.InfoCalls);
        Assert.Equal(1, server.Aborted);
        await AssertDiscardedAsync(pool, () => server.Opened, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerKeepsTheDataConnectionOpenAfterTheLastByte_EndsAtTheReadTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(200_000, seed: 83);
        var server = new ScriptedTransferServer(content) { ReadTimeout = TimeSpan.FromSeconds(1) };
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.StallIgnoringToken, content.Length));
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(84));

        // The last read waited for the probe beyond the size, which only the read timeout ended.
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(900), watch.Elapsed.ToString());
        Assert.Null(failure);
        AssertSameBytes(content, data);
        await AssertEndsAsync(stream);
        Assert.Equal(new long[] { 0 }, server.TransferOffsets);
        Assert.Empty(retries.Infos);
        await AssertDiscardedAsync(pool, () => server.Opened, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task DropOneByteBeforeTheEnd_IsResumedFromThatByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 85);
        var server = new ScriptedTransferServer(content);
        server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, content.Length - 1));
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(86));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { 0, content.Length - 1 }, server.TransferOffsets);
        Assert.Single(retries.Infos);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerSendsMore_FailsAfterEveryByte_WithoutResuming()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 87);
        var server = new ScriptedTransferServer(content) { ExtraBytes = 1 };
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);

        var (data, failure) = ReadAll(stream, new Random(88));

        AssertSameBytes(content, data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.Equal(1, exhausted.Attempts);
        Assert.Contains("sent more than", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal(new long[] { 0 }, server.TransferOffsets);
        Assert.Empty(retries.Infos);
        Assert.Equal(0, server.InfoCalls);
        await AssertEndsAsync(stream);
        await AssertDiscardedAsync(pool, () => server.Opened, () => server.Active);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_ServerKeepsTheDataConnectionOpenAfterTheLastByte_EndsAtTheReadTimeoutWithoutResuming()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 89);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;

        // The whole file, and then both connections open and silent.
        server.StallTransferAfter = content.Length;
        var retries = new Retries(3);
        using var pool = LoopbackPool(server, TimeSpan.FromMilliseconds(500));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(90));

        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(450), watch.Elapsed.ToString());
        Assert.Null(failure);
        AssertSameBytes(content, data);
        await AssertEndsAsync(stream);
        Assert.Equal(1, server.Commands.Count(command => command == "RETR"));
        Assert.Empty(server.Restarts);
        Assert.Empty(retries.Infos);
        await WaitUntilAsync(() => server.DataConnectionsClosedByClient == 1, "the client to close the silent data connection");
        await AssertDiscardedAsync(pool, () => server.ControlConnections, () => 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_StallOneByteBeforeTheEnd_IsResumedFromThatByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 91);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;

        // The first transfer stalls one byte before the end; the second sends that byte and stalls
        // after it, which ends the stream.
        server.StallTransferAfter = content.Length - 1;
        var retries = new Retries(1);
        using var pool = LoopbackPool(server, TimeSpan.FromMilliseconds(500));
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(92));

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.Equal(new long[] { content.Length - 1 }, server.Restarts);
        Assert.Equal(2, server.Commands.Count(command => command == "RETR"));
        Assert.Single(retries.Infos);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => 0);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_ServerSendsMore_FailsAfterEveryByte_WithoutResuming()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 93);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + FilePath] = content;
        server.ExtraBytesAfterFile = 1;
        var retries = new Retries(3);
        using var pool = LoopbackPool(server, TimeSpan.FromSeconds(30));
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, Version(content.Length), ct);

        var (data, failure) = ReadAll(stream, new Random(94));

        AssertSameBytes(content, data);
        Assert.Equal(1, Assert.IsType<DatasusRetriesExhaustedException>(failure).Attempts);
        Assert.Equal(1, server.Commands.Count(command => command == "RETR"));
        Assert.Empty(retries.Infos);
        await AssertEndsAsync(stream);
        await AssertDiscardedAsync(pool, () => server.ControlConnections, () => 0);
    }

    // A consumer that pauses for longer than the server keeps an idle transfer: the pump has filled
    // the buffer and waits for room, the server closes the data and the control connections in the
    // meantime, and the pump's next read, once the consumer frees room, finds them closed. The
    // pause is the test's signal, not time.
    [Fact(Timeout = 30_000)]
    public async Task LongPause_TheServerClosesTheIdleConnections_ResumesOnANewConnectionInTheSamePlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3 * Capacity, seed: 95);
        var server = new ScriptedTransferServer(content);
        var retries = new Retries(3);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);
        var first = ReadExactly(stream, 100_000);
        await WaitUntilAsync(() => server.BytesServed == 100_000 + Capacity, "the pump to fill the buffer again");

        // During the pause the place stays the stream's.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pool.AcquireAsync(wait.Token));
        }

        server.CloseIdleConnections();
        var (rest, failure) = ReadAll(stream, new Random(96));

        Assert.Null(failure);
        AssertSameBytes(content, [.. first, .. rest]);
        Assert.Equal(new long[] { 0, 100_000 + Capacity }, server.TransferOffsets);
        Assert.Equal(2, server.Opened);
        Assert.Equal(1, server.InfoCalls);
        var retry = Assert.Single(retries.Infos);
        Assert.IsType<IOException>(retry.Exception.InnerException);
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    // A known limitation, not the behavior wanted: each pause longer than the server keeps an idle
    // connection costs an attempt, and the count starts again only after ResetAfterBytes, so a
    // consumer that reads less than that between its pauses runs out of attempts against a healthy
    // server. Letting the connection go during a pause is left for a later stage.
    [Fact(Timeout = 30_000)]
    public async Task RepeatedLongPauses_WithLittleReadBetweenThem_RunOutOfAttempts_AKnownLimitation()
    {
        var ct = TestContext.Current.CancellationToken;
        const int MaxRetries = 2;
        var content = Content(3 * Capacity, seed: 97);
        var server = new ScriptedTransferServer(content);
        var retries = new Retries(MaxRetries);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);
        var data = new MemoryStream();

        // A pause with the buffer full, then a read of 100,000 bytes, which makes the pump read the
        // closed transfer; once more than the retries allow.
        for (var pause = 0; pause <= MaxRetries; pause++)
        {
            await WaitUntilAsync(() => server.BytesServed == data.Length + Capacity, "the pump to fill the buffer");
            server.CloseIdleConnections();
            data.Write(ReadExactly(stream, 100_000), 0, 100_000);
        }

        var (rest, failure) = ReadAll(stream, new Random(98));
        data.Write(rest, 0, rest.Length);

        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.Equal(MaxRetries + 1, exhausted.Attempts);
        AssertSameBytes(content.Take(Capacity + 200_000).ToArray(), data.ToArray());
        Assert.Equal(new long[] { 0, Capacity, Capacity + 100_000 }, server.TransferOffsets);
        Assert.Equal(FirstTwoAttempts, retries.Infos.Select(info => info.Attempt));
        await AssertEndsAsync(stream);
        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    // What a .dbc consumer sees at the end of a transfer. The DbfReader drains the DBF stream after
    // the last record, but that stream ends at the end code of the compressed data without reading
    // its source again: neither bytes beyond the size nor a failure after the last byte reach the
    // reader. Where the server fails or sends more, the pump is over before the reader starts, so
    // a read beyond the end code would meet the failure; where it keeps silent, the reader ends
    // long before the read timeout.
    [Theory(Timeout = 30_000)]
    [InlineData("EQRR0509.dbc", "SendsMore")]
    [InlineData("EQRR0509.dbc", "FailsAfter")]
    [InlineData("EQRR0509.dbc", "SilentAfter")]
    [InlineData("LTRR0610.dbc", "SendsMore")]
    [InlineData("LTRR0610.dbc", "FailsAfter")]
    [InlineData("LTRR0610.dbc", "SilentAfter")]
    public async Task CommittedFixture_AfterTheLastByte_TheReaderNeverReadsTheSourceAgain(string file, string end)
    {
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(RepositoryPaths.Fixtures, file);
        var dbc = File.ReadAllBytes(path);
        var server = new ScriptedTransferServer(dbc)
        {
            ExtraBytes = end == "SendsMore" ? 100 : 0,
            ReadTimeout = TimeSpan.FromSeconds(60),
        };
        if (end != "SendsMore")
        {
            server.Faults.Enqueue(new ScriptedFault(end == "FailsAfter" ? ScriptedFaultKind.Drop : ScriptedFaultKind.StallIgnoringToken, dbc.Length));
        }

        var retries = new Retries(2);
        using var pool = new ConnectionPool(server.Factory(), 1);
        var mismatches = 0;
        var options = new DbSus.Dbc.DbcReadOptions { ChecksumMismatch = _ => mismatches++ };
        var expected = Records(DbSusFile.OpenDbcReader(path));
        var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);
        if (end != "SilentAfter")
        {
            await AssertEndsAsync(stream);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var actual = Records(DbSusFile.OpenDbcReader(stream, dbcOptions: options));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"{file}: record {i + 1} differs.");
        }

        Assert.Equal(0, mismatches);
        await AssertEndsAsync(stream);
        Assert.Equal(1, server.Transfers);
        Assert.Empty(retries.Infos);

        // The failure the reader never met: the same transfer, read to its end.
        if (end == "SendsMore")
        {
            using var again = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, Remote, server.Version, ct);
            var (data, failure) = ReadAll(again, new Random(99));
            AssertSameBytes(dbc, data);
            Assert.IsType<DatasusRetriesExhaustedException>(failure);
        }

        await AssertNothingHeldAsync(pool, () => server.Active);
    }

    private static async Task WithinAsync(Task task, CancellationToken cancellationToken)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)) == task, "Timed out waiting.");
        await task;
    }

    private static async Task UntilAsync(Task release, CancellationToken cancellationToken)
    {
        await Task.WhenAny(release, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    // The place is free (a lease comes at once), and once the pool lets go of its idle connections,
    // none is left open.
    private static async Task AssertNothingHeldAsync(ConnectionPool pool, Func<int> openConnections)
    {
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (await pool.AcquireAsync(timeout.Token))
        {
        }

        pool.Dispose();
        Assert.Equal(0, openConnections());
    }

    // The place is free, and the next lease makes a new connection: the stream did not give its
    // connection back to the pool.
    private static async Task AssertDiscardedAsync(ConnectionPool pool, Func<int> connectionsMade, Func<int> openConnections)
    {
        var made = connectionsMade();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (await pool.AcquireAsync(timeout.Token))
        {
            Assert.Equal(made + 1, connectionsMade());
        }

        pool.Dispose();
        Assert.Equal(0, openConnections());
    }

    private static List<byte[]> Records(DbSus.Dbf.DbfReader reader)
    {
        using (reader)
        {
            var records = new List<byte[]>();
            while (reader.Read())
            {
                var raw = new MemoryStream();
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    var bytes = reader.GetRawBytes(ordinal).ToArray();
                    raw.Write(bytes, 0, bytes.Length);
                }

                records.Add(raw.ToArray());
            }

            return records;
        }
    }

    private static ConnectionPool LoopbackPool(LoopbackFtpServer server, TimeSpan readTimeout) => new(
        new FtpConnectionFactory(new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = readTimeout,
        }),
        1);

    // A retry policy from 100 ms that records what it reports to OnRetry; the waits are not waited
    // unless a test gives its own.
    private sealed class Retries
    {
        private readonly List<DatasusRetryInfo> _infos = [];

        public Retries(int maxRetries, Func<TimeSpan, CancellationToken, Task>? delay = null) =>
            Policy = new RetryPolicy(maxRetries, TimeSpan.FromMilliseconds(100), delay ?? ((_, _) => Task.CompletedTask), info =>
            {
                lock (_infos)
                {
                    _infos.Add(info);
                }
            });

        public RetryPolicy Policy { get; }

        public List<DatasusRetryInfo> Infos
        {
            get
            {
                lock (_infos)
                {
                    return [.. _infos];
                }
            }
        }
    }
}
