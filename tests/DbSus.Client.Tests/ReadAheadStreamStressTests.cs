using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// Opens, reads, cancels and disposes the read-ahead stream at random points, against slow servers
// and servers that drop, stall, end early or send too much. Every iteration must deliver a prefix
// of the file, end only in an outcome its scenario allows, end its pump, and free its place in
// the pool. The seed is fixed; a failure names the seed of its iteration, which replays it alone.
public sealed class ReadAheadStreamStressTests
{
    private const int Seed = 20261021;
    private const int Capacity = RemoteReadAheadStream.Capacity;

    private enum Fault
    {
        None,
        Drop,
        Stall,
        StallIgnoringToken,
        EndEarly,
        Extra,
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
            ReadTimeout = scenario.Stalls ? TimeSpan.FromMilliseconds(40) : TimeSpan.FromSeconds(30),
            DropAfter = scenario.Fault == Fault.Drop ? scenario.FaultAt : null,
            StallAfter = scenario.Stalls ? scenario.FaultAt : null,
            StallIgnoresToken = scenario.Fault == Fault.StallIgnoringToken,
            EndAfter = scenario.Fault == Fault.EndEarly ? scenario.FaultAt : null,
            ExtraBytes = scenario.Fault == Fault.Extra ? 1 + (scenario.Seed % 5000) : 0,
            BeforeRead = scenario.Slow ? SlowAsync : null,
        };
        using var pool = new ConnectionPool(server.Factory(), 1);
        var ended = await ConsumeAsync(scenario, content, pool, scenario.Size);
        Assert.True(server.Active <= 1, $"{server.Active} connections are open.");
        if (ended)
        {
            // The consumer saw the end only after the whole transfer; the connection was kept.
            Assert.Equal(0, server.Aborted);
        }
    }

    private static async Task RunLoopbackAsync(Scenario scenario)
    {
        var content = Content(scenario.Size, scenario.Seed);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files["/dissemin/publicos/X/A.dbc"] = content;
        server.StallTransferAfter = scenario.Stalls ? scenario.FaultAt : null;
        server.ExtraBytesAfterFile = scenario.Fault == Fault.Extra ? 1 + (scenario.Seed % 100_000) : null;
        var options = new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = scenario.Stalls ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(30),
        };
        using var pool = new ConnectionPool(new FtpConnectionFactory(options), 1);
        var size = scenario.Fault == Fault.EndEarly ? scenario.Size + 10 : scenario.Size;
        await ConsumeAsync(scenario, content, pool, size);
    }

    // Runs the consumer's side of a scenario and checks what any server must give it; true when
    // the stream reached its end.
    private static async Task<bool> ConsumeAsync(Scenario scenario, byte[] content, ConnectionPool pool, long size)
    {
        using var cancellation = new CancellationTokenSource();
        var stream = await RemoteReadAheadStream.OpenAsync(pool, RemotePath.Parse("/X/A.dbc", "/dissemin/publicos"), size, cancellation.Token);
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
            Assert.Equal(Fault.None, scenario.Fault);
        }
        else if (scenario.Fault == Fault.None && scenario.Step == Step.ReadAll)
        {
            Assert.Fail($"The stream did not reach its end: {failure}");
        }

        switch (failure)
        {
            case null:
                break;
            case OperationCanceledException:
                Assert.True(scenario.Step is Step.CancelAfter or Step.CancelFromAnotherThread, $"Canceled without a cancellation: {failure}");
                break;
            case ObjectDisposedException:
                Assert.True(scenario.Step == Step.DisposeFromAnotherThread, $"Disposed without a Dispose: {failure}");
                break;
            case DatasusRetriesExhaustedException:
                Assert.True(scenario.Fault != Fault.None, $"A failure from a server without faults: {failure}");
                break;
            default:
                Assert.Fail($"An unexpected failure: {failure}");
                break;
        }

        await AssertEndsAsync(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using (await pool.AcquireAsync(timeout.Token))
        {
        }

        return ended;
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

    private sealed class Scenario
    {
        public Scenario(int seed, bool loopback)
        {
            Seed = seed;
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
            var faults = loopback ? new[] { Fault.Stall, Fault.EndEarly, Fault.Extra } : new[] { Fault.Drop, Fault.Stall, Fault.StallIgnoringToken, Fault.EndEarly, Fault.Extra };
            Fault = random.Next(10) < 4 ? Fault.None : faults[random.Next(faults.Length)];
            // An empty file cannot drop, stall or end early within itself; over the loopback server,
            // ending early means announcing more than the file has.
            if (Size == 0 && Fault is not (Fault.None or Fault.Extra) && !(loopback && Fault == Fault.EndEarly))
            {
                Fault = Fault.None;
            }

            FaultAt = Size == 0 ? 0 : random.Next(0, Size);
            Step = (Step)random.Next(5);
            StepAt = random.Next(0, Size + 1);
            DelayMs = random.Next(0, 20);
            ReadChunk = random.Next(1, 100_000);
        }

        public int Seed { get; }

        public int Size { get; }

        public int MaxChunk { get; }

        public bool Slow { get; }

        public Fault Fault { get; }

        public int FaultAt { get; }

        public Step Step { get; }

        public int StepAt { get; }

        public int DelayMs { get; }

        public int ReadChunk { get; }

        public bool Stalls => Fault is Fault.Stall or Fault.StallIgnoringToken;

        public override string ToString() =>
            $"size {Size}, server chunk {MaxChunk}, slow {Slow}, fault {Fault} at {FaultAt}, step {Step} at {StepAt}, delay {DelayMs} ms, read chunk {ReadChunk}";
    }
}
