using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// Opens, reads, cancels and disposes the read-ahead stream at random points, against slow servers
// and servers that drop, stall or end early once, in a row or on every transfer, send too much,
// fail after the last byte, or republish the file while the stream waits to resume. Every
// iteration must deliver a prefix of the file, end only in an outcome its scenario allows, end its
// pump, free its place in the pool and leave no connection open. The seed is fixed; a failure
// names the seed of its iteration, which replays it alone.
public sealed class ReadAheadStreamStressTests
{
    private const int Seed = 20261021;
    private const int Capacity = RemoteReadAheadStream.Capacity;

    private enum Fault
    {
        None,

        // Once: resumed when there is a retry.
        DropOnce,
        StallOnce,
        StallIgnoringTokenOnce,
        EndEarlyOnce,

        // Several drops, each soon after the last: resumed until the attempts in a row run out.
        DropsInARow,

        // At the same place of the file on every transfer: the attempts run out.
        DropAlways,
        StallAlways,
        EndEarlyAlways,

        // Terminal: more than the size, or a drop and a new version before the resumption.
        Extra,
        VersionChanged,

        // A drop or a stall where the pump reads beyond the size: an end, never resumed.
        AfterTheLastByte,
    }

    private enum Step
    {
        ReadAll,
        DisposeAfter,
        CancelAfter,
        CancelFromAnotherThread,
        DisposeFromAnotherThread,
    }

    [Fact(Timeout = 120_000)]
    public async Task ScriptedServer_RandomPoints_NeverHangNorEndOutsideTheScenario()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeds = new Random(Seed);
        for (var i = 0; i < 400; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scenario = new Scenario(seeds.Next(), loopback: false);
            await RunAsync(i, scenario, () => RunScriptedAsync(scenario));
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task LoopbackServer_RandomPoints_NeverHangNorEndOutsideTheScenario()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeds = new Random(Seed + 1);
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var scenario = new Scenario(seeds.Next(), loopback: true);
            await RunAsync(i, scenario, () => RunLoopbackAsync(scenario));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "Any failure of an iteration is reported with its seed.")]
    private static async Task RunAsync(int iteration, Scenario scenario, Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (Exception exception)
        {
            Assert.Fail($"Stress seed {Seed}, iteration {iteration}, iteration seed {scenario.Seed} ({scenario}): {exception}");
        }
    }

    private static async Task RunScriptedAsync(Scenario scenario)
    {
        var content = Content(scenario.Size, scenario.Seed);
        var server = new ScriptedTransferServer(content)
        {
            MaxChunk = scenario.MaxChunk,
            ReadTimeout = scenario.Fault switch
            {
                Fault.StallOnce or Fault.StallIgnoringTokenOnce or Fault.AfterTheLastByte => TimeSpan.FromMilliseconds(150),
                Fault.StallAlways => TimeSpan.FromMilliseconds(40),
                _ => TimeSpan.FromSeconds(30),
            },
            DropAfter = scenario.Fault == Fault.DropAlways ? scenario.FaultAt : null,
            StallAfter = scenario.Fault == Fault.StallAlways ? scenario.FaultAt : null,
            StallIgnoresToken = scenario.Fault == Fault.StallAlways && scenario.Seed % 2 == 1,
            EndAfter = scenario.Fault == Fault.EndEarlyAlways ? scenario.FaultAt : null,
            ExtraBytes = scenario.Fault == Fault.Extra ? 1 + (scenario.Seed % 5000) : 0,
            BeforeRead = scenario.Slow ? SlowAsync : null,
        };
        switch (scenario.Fault)
        {
            case Fault.DropOnce or Fault.VersionChanged:
                server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, scenario.FaultAt));
                break;
            case Fault.StallOnce:
                server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Stall, scenario.FaultAt));
                break;
            case Fault.StallIgnoringTokenOnce:
                server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.StallIgnoringToken, scenario.FaultAt));
                break;
            case Fault.EndEarlyOnce:
                server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.EndEarly, scenario.FaultAt));
                break;
            case Fault.AfterTheLastByte:
                server.Faults.Enqueue(new ScriptedFault(scenario.Seed % 2 == 0 ? ScriptedFaultKind.Drop : ScriptedFaultKind.Stall, scenario.Size));
                break;
            case Fault.DropsInARow:
                server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, scenario.FaultAt));
                for (var i = 1; i < scenario.Drops; i++)
                {
                    server.Faults.Enqueue(new ScriptedFault(ScriptedFaultKind.Drop, scenario.Seed % 200_000));
                }

                break;
        }

        var retries = new RetryCount();
        var policy = retries.Policy(scenario, () => server.ModifiedUtc = server.ModifiedUtc.AddMinutes(1));
        using var pool = new ConnectionPool(server.Factory(), 1);
        var (ended, failure) = await ConsumeAsync(scenario, content, pool, policy, server.Version);
        Check(scenario, ended, failure, retries.Count);
        pool.Dispose();
        Assert.True(server.Active == 0, $"{server.Active} connections are left open.");
    }

    private static async Task RunLoopbackAsync(Scenario scenario)
    {
        var content = Content(scenario.Size, scenario.Seed);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files["/dissemin/publicos/X/A.dbc"] = content;
        server.StallTransferAfter = scenario.Fault is Fault.StallAlways or Fault.VersionChanged ? scenario.FaultAt : null;
        server.ExtraBytesAfterFile = scenario.Fault == Fault.Extra ? 1 + (scenario.Seed % 100_000) : null;
        var options = new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = server.StallTransferAfter is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(250),
        };
        var retries = new RetryCount();
        var policy = retries.Policy(scenario, () =>
        {
            // Republished while the stream waits: another size, and no stall any more.
            server.StallTransferAfter = null;
            server.Files["/dissemin/publicos/X/A.dbc"] = Content(scenario.Size + 1, scenario.Seed);
        });
        using var pool = new ConnectionPool(new FtpConnectionFactory(options), 1);
        var size = scenario.Fault == Fault.EndEarlyAlways ? scenario.Size + 10 : scenario.Size;
        var (ended, failure) = await ConsumeAsync(scenario, content, pool, policy, Version(size));
        Check(scenario, ended, failure, retries.Count);
    }

    // Runs the consumer's side of a scenario and checks what any server must give it: a prefix of
    // the file, the pump over and the place free. Returns whether the stream reached its end, and
    // how it failed.
    private static async Task<(bool Ended, Exception? Failure)> ConsumeAsync(Scenario scenario, byte[] content, ConnectionPool pool, RetryPolicy policy, RemoteFileInfo version)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = await RemoteReadAheadStream.OpenAsync(pool, policy, RemotePath.Parse("/X/A.dbc", "/dissemin/publicos"), version, cancellation.Token);
        var other = scenario.Step switch
        {
            Step.CancelFromAnotherThread => Task.Run(async () =>
            {
                await Task.Delay(scenario.DelayMs, CancellationToken.None);
                cancellation.Cancel();
            }),
            Step.DisposeFromAnotherThread => Task.Run(async () =>
            {
                await Task.Delay(scenario.DelayMs, CancellationToken.None);
                stream.Dispose();
            }),
            _ => Task.CompletedTask,
        };

        var random = new Random(scenario.Seed + 1);
        var data = new MemoryStream();
        var buffer = new byte[scenario.ReadChunk];
        Exception? failure = null;
        var ended = false;
        var pauses = 0;
        try
        {
            while (true)
            {
                if (scenario.Step == Step.DisposeAfter && data.Length >= scenario.StepAt)
                {
                    stream.Dispose();
                    break;
                }

                if (scenario.Step == Step.CancelAfter && data.Length >= scenario.StepAt)
                {
                    cancellation.Cancel();
                }

                var read = stream.Read(buffer, 0, random.Next(1, buffer.Length + 1));
                if (read == 0)
                {
                    ended = true;
                    break;
                }

                data.Write(buffer, 0, read);
                // A slow consumer, a few times per iteration: on Windows a pause lasts up to 15 ms.
                if (pauses < 10 && random.Next(50) == 0)
                {
                    pauses++;
                    Thread.Sleep(random.Next(2));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            failure = exception;
        }

        await other;
        stream.Dispose();

        var received = data.ToArray();
        Assert.True(received.Length <= content.Length, $"{received.Length} bytes received of a file of {content.Length}.");
        AssertSameBytes(content.Take(received.Length).ToArray(), received);
        if (ended)
        {
            Assert.Equal(content.Length, received.Length);
        }

        await AssertEndsAsync(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (await pool.AcquireAsync(timeout.Token))
        {
        }

        return (ended, failure);
    }

    // What the scenario allows: the outcome, and when the attempts run out, how many there were.
    private static void Check(Scenario scenario, bool ended, Exception? failure, int retries)
    {
        var retried = scenario.MaxRetries > 0;
        var resumable = scenario.Fault is Fault.DropOnce or Fault.StallOnce or Fault.StallIgnoringTokenOnce or Fault.EndEarlyOnce;
        // Over the loopback server, every transfer stalls that many bytes after where it started,
        // and one that reaches the end of the file stalls there, which ends the stream: it gets
        // there when the retries cover the transfers it takes.
        var stallsToTheEnd = scenario.Loopback && scenario.Fault == Fault.StallAlways && scenario.FaultAt > 0
                             && ((scenario.Size + scenario.FaultAt - 1) / scenario.FaultAt) - 1 <= scenario.MaxRetries;
        var mustEnd = scenario.Fault is Fault.None or Fault.AfterTheLastByte || (resumable && retried && !scenario.Loopback) || stallsToTheEnd;
        var mayEnd = mustEnd || scenario.Fault == Fault.DropsInARow;
        // Over the loopback server, ending early is announcing more than the file has: a resumption
        // finds the true size, which is another version.
        var announcedMore = scenario.Loopback && scenario.Fault == Fault.EndEarlyAlways;
        var runsOut = !announcedMore && !stallsToTheEnd && scenario.Fault is Fault.DropAlways or Fault.StallAlways or Fault.EndEarlyAlways;
        var changes = (scenario.Fault == Fault.VersionChanged || announcedMore) && retried;

        if (ended)
        {
            Assert.True(mayEnd, "The stream reached its end, which its scenario does not allow.");
        }
        else if (scenario.Step == Step.ReadAll)
        {
            Assert.False(mustEnd, $"The stream did not reach its end: {failure}");
        }

        switch (failure)
        {
            case null:
                break;
            case OperationCanceledException:
                Assert.True(scenario.Step is Step.CancelAfter or Step.CancelFromAnotherThread, $"Canceled without a cancellation: {failure}");
                return;
            case ObjectDisposedException:
                Assert.True(scenario.Step == Step.DisposeFromAnotherThread, $"Disposed without a Dispose: {failure}");
                return;
            case DatasusRemoteFileChangedException:
                Assert.True(changes, $"A new version where the scenario has none: {failure}");
                return;
            case DatasusRetriesExhaustedException exhausted:
                Assert.True(!mustEnd && !changes, $"Attempts ran out where the scenario allows none to: {failure}");
                if (runsOut || scenario.Fault == Fault.DropsInARow)
                {
                    Assert.Equal(scenario.MaxRetries + 1, exhausted.Attempts);
                    Assert.True(retries >= scenario.MaxRetries, $"{retries} retries reported for {exhausted.Attempts} attempts.");
                }
                else if (scenario.Fault == Fault.Extra || ((resumable || announcedMore) && !retried))
                {
                    Assert.Equal(1, exhausted.Attempts);
                }

                return;
            default:
                Assert.Fail($"An unexpected failure: {failure}");
                return;
        }

        // No failure: the stream ended, or the consumer stopped first.
        if (!ended && scenario.Step == Step.ReadAll)
        {
            Assert.Fail("The stream neither ended nor failed.");
        }
    }

    private static async Task SlowAsync(int read, CancellationToken cancellationToken)
    {
        if (read % 256 == 0)
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
        else if (read % 4 == 0)
        {
            await Task.Yield();
        }
    }

    // Counts the retries the policy reports; its waits are short and real, so that a stop can come
    // during one, and the scenario's new version is published during the first.
    private sealed class RetryCount
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public RetryPolicy Policy(Scenario scenario, Action republish) => new(
            scenario.MaxRetries,
            TimeSpan.FromMilliseconds(1),
            (_, token) =>
            {
                if (scenario.Fault == Fault.VersionChanged)
                {
                    republish();
                }

                return scenario.BackoffMs == 0 ? Task.CompletedTask : Task.Delay(scenario.BackoffMs, token);
            },
            _ => Interlocked.Increment(ref _count));
    }

    private sealed class Scenario
    {
        public Scenario(int seed, bool loopback)
        {
            Seed = seed;
            Loopback = loopback;
            var random = new Random(seed);
            Size = random.Next(10) switch
            {
                0 => 0,
                < 4 => random.Next(1, 20_000),
                < 8 => random.Next(20_000, 300_000),
                _ => random.Next(Capacity + 1, 2 * Capacity),
            };
            MaxChunk = random.Next(512, 65_537);
            Slow = random.Next(4) == 0;
            var faults = loopback
                ? new[] { Fault.StallAlways, Fault.EndEarlyAlways, Fault.Extra, Fault.VersionChanged }
                : new[]
                {
                    Fault.DropOnce, Fault.StallOnce, Fault.StallIgnoringTokenOnce, Fault.EndEarlyOnce, Fault.DropsInARow,
                    Fault.DropAlways, Fault.StallAlways, Fault.EndEarlyAlways, Fault.Extra, Fault.VersionChanged,
                    Fault.AfterTheLastByte,
                };
            Fault = random.Next(10) < 3 ? Fault.None : faults[random.Next(faults.Length)];

            // An empty file cannot fail within itself, only after its end; over the loopback server,
            // ending early means announcing more than the file has.
            if (Size == 0 && Fault is not (Fault.None or Fault.Extra or Fault.AfterTheLastByte) && !(loopback && Fault == Fault.EndEarlyAlways))
            {
                Fault = Fault.None;
            }

            FaultAt = Size == 0 ? 0 : random.Next(0, Size);
            Drops = random.Next(2, 5);
            MaxRetries = random.Next(3);
            BackoffMs = random.Next(3) == 0 ? random.Next(1, 3) : 0;
            Step = (Step)random.Next(5);
            StepAt = random.Next(0, Size + 1);
            DelayMs = random.Next(0, 20);
            ReadChunk = random.Next(1, 100_000);
        }

        public int Seed { get; }

        public bool Loopback { get; }

        public int Size { get; }

        public int MaxChunk { get; }

        public bool Slow { get; }

        public Fault Fault { get; }

        public int FaultAt { get; }

        public int Drops { get; }

        public int MaxRetries { get; }

        public int BackoffMs { get; }

        public Step Step { get; }

        public int StepAt { get; }

        public int DelayMs { get; }

        public int ReadChunk { get; }

        public override string ToString() =>
            $"size {Size}, server chunk {MaxChunk}, slow {Slow}, fault {Fault} at {FaultAt} ({Drops} drops in a row if any), {MaxRetries} retries, backoff {BackoffMs} ms, step {Step} at {StepAt}, delay {DelayMs} ms, read chunk {ReadChunk}";
    }
}
