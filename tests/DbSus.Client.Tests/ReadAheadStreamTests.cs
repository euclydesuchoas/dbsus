using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// The stream that reads a remote file ahead of its consumer: every byte once and in order, nothing
// beyond the announced size, a bounded buffer, and a connection that goes back to the pool only
// after a whole transfer, whatever ends it (the end, a failure, Dispose or the token).
public sealed class ReadAheadStreamTests
{
    private const string FilePath = "/X/A.dbc";
    private const int Capacity = RemoteReadAheadStream.Capacity;

    [Fact(Timeout = 30_000)]
    public async Task ReadsTheFileByteForByte_AndGivesTheConnectionBack()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((3 * Capacity) + 123, seed: 1);
        var server = new FakeServer();
        server.Add(FilePath, content);
        using var pool = new ConnectionPool(server.Factory(), 1);

        using (var stream = await Open(pool, FakeServer.Root, content.Length, ct))
        {
            var (data, failure) = ReadAll(stream, new Random(2));
            Assert.Null(failure);
            AssertSameBytes(content, data);
            Assert.Equal(0, stream.Read(new byte[10], 0, 10));
            await AssertEndsAsync(stream);
        }

        using (var again = await Open(pool, FakeServer.Root, content.Length, ct))
        {
            AssertSameBytes(content, ReadAll(again, new Random(3)).Data);
        }

        Assert.Equal(1, server.ConnectionsOpened);
    }

    [Fact(Timeout = 30_000)]
    public async Task EmptyFile_EndsAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeServer();
        server.Add(FilePath, []);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, 0, ct);

        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
        await AssertEndsAsync(stream);
        Assert.Equal(1, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerSendsMore_TheExcessIsNeverDelivered()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(Capacity + 5000, seed: 4);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.SendExtraBytes(1, 10_000);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, content.Length, ct);

        var (data, failure) = ReadAll(stream, new Random(5));

        AssertSameBytes(content, data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.Contains("sent more than", exhausted.Message, StringComparison.Ordinal);
        Assert.Equal(FilePath, exhausted.RemotePath);
        Assert.Equal(1, exhausted.Attempts);
        Assert.IsType<TransientTransportException>(exhausted.InnerException);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task TransferEndsBeforeTheSize_FailsAfterTheBytesItHad()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(200_000, seed: 6);
        var server = new FakeServer();
        server.Add(FilePath, content);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, content.Length + 1000, ct);

        var (data, failure) = ReadAll(stream, new Random(7));

        AssertSameBytes(content, data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.Contains($"Received {content.Length} of {content.Length + 1000} bytes", exhausted.Message, StringComparison.Ordinal);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task ConnectionDrops_FailsAfterTheBytesBeforeTheDrop()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 8);
        var server = new FakeServer();
        server.Add(FilePath, content);
        server.FailNextTransferAfter(100_000);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, content.Length, ct);

        var (data, failure) = ReadAll(stream, new Random(9));

        AssertSameBytes(content.Take(100_000).ToArray(), data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.IsType<IOException>(exhausted.InnerException!.InnerException);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposedMidTransfer_DiscardsTheConnectionAndFreesItsPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3 * Capacity, seed: 10);
        var server = new FakeServer();
        server.Add(FilePath, content);
        using var pool = new ConnectionPool(server.Factory(), 1);
        var stream = await Open(pool, FakeServer.Root, content.Length, ct);
        Assert.Equal(10_000, ReadExactly(stream, 10_000).Length);

        stream.Dispose();

        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using (await pool.AcquireAsync(timeout.Token))
        {
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Buffer_NeverHoldsMoreThanItsCapacity()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((4 * Capacity) + 77, seed: 11);
        var server = new FakeServer();
        server.Add(FilePath, content);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, content.Length, ct);

        // Nothing is read yet: the pump fills the buffer and stops there.
        await WaitUntilAsync(() => server.BytesServed == Capacity, "the pump to fill the buffer");
        await Task.Delay(200, ct);
        Assert.Equal(Capacity, server.BytesServed);
        Assert.Equal(Capacity, stream.MaxBuffered);

        var random = new Random(12);
        var data = new List<byte>(content.Length);
        var buffer = new byte[100_000];
        int read;
        while ((read = stream.Read(buffer, 0, random.Next(1, buffer.Length))) > 0)
        {
            data.AddRange(buffer.Take(read));
            Assert.True(server.BytesServed - data.Count <= Capacity, $"{server.BytesServed - data.Count} bytes ahead of the consumer.");
        }

        AssertSameBytes(content, [.. data]);
        Assert.Equal(Capacity, stream.MaxBuffered);
    }

    [Fact(Timeout = 60_000)]
    public async Task SlowConsumer_LosesNorRepeatsAnyByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((2 * Capacity) + 999, seed: 13);
        var server = new ScriptedTransferServer(content)
        {
            MaxChunk = 20_000,
            BeforeRead = (read, token) => read % 7 == 0 ? Task.Delay(1, token) : Task.CompletedTask,
        };
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);
        var random = new Random(14);

        var (data, failure) = ReadAll(stream, random, maxChunk: 30_000, between: () =>
        {
            if (random.Next(20) == 0)
            {
                Thread.Sleep(random.Next(3));
            }
        });

        Assert.Null(failure);
        AssertSameBytes(content, data);
        Assert.True(stream.MaxBuffered <= Capacity);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.Aborted);
        Assert.Equal(1, server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task ReadAfterDispose_ThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeServer();
        server.Add(FilePath, Content(1000, seed: 15));
        using var pool = new ConnectionPool(server.Factory(), 1);
        var stream = await Open(pool, FakeServer.Root, 1000, ct);

        stream.Dispose();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[10], 0, 10));
        Assert.Throws<ObjectDisposedException>(() => stream.Read(new byte[10], 0, 0));
        Assert.False(stream.CanRead);
        await AssertEndsAsync(stream);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canceled_WhileTheReadWaitsForTheNetwork(bool stallIgnoresToken)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(500_000, seed: 16);
        var server = new ScriptedTransferServer(content) { StallAfter = 10_000, StallIgnoresToken = stallIgnoresToken };
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, cancellation.Token);
        Assert.Equal(10_000, ReadExactly(stream, 10_000).Length);
        var blocked = Task.Run(() => stream.Read(new byte[100], 0, 100), ct);
        await Task.Delay(100, ct);
        Assert.False(blocked.IsCompleted);

        cancellation.Cancel();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        await AssertEndsAsync(stream);
        Assert.Equal(1, server.Aborted);
        Assert.Equal(0, server.Active);
        Assert.Throws<OperationCanceledException>(() => stream.Read(new byte[10], 0, 10));
    }

    [Fact(Timeout = 30_000)]
    public async Task Canceled_WhileThePumpWaitsForRoom()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3 * Capacity, seed: 17);
        var server = new FakeServer();
        server.Add(FilePath, content);
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stream = await Open(pool, FakeServer.Root, content.Length, cancellation.Token);
        await WaitUntilAsync(() => stream.MaxBuffered == Capacity, "the pump to fill the buffer");

        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => stream.Read(new byte[10], 0, 10));
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposedWhileThePumpWaitsForRoom_ClosesTheConnectionByForce()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(3 * Capacity, seed: 18);
        var server = new ScriptedTransferServer(content);
        using var pool = new ConnectionPool(server.Factory(), 1);
        var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);
        await WaitUntilAsync(() => stream.MaxBuffered == Capacity, "the pump to fill the buffer");

        stream.Dispose();

        await AssertEndsAsync(stream);
        Assert.Equal(1, server.Aborted);
        Assert.Equal(0, server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisposedFromAnotherThread_EndsABlockedRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(100_000, seed: 19);
        var server = new ScriptedTransferServer(content) { StallAfter = 5000 };
        using var pool = new ConnectionPool(server.Factory(), 1);
        var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);
        Assert.Equal(5000, ReadExactly(stream, 5000).Length);
        var blocked = Task.Run(() => stream.Read(new byte[100], 0, 100), ct);
        await Task.Delay(100, ct);

        stream.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => blocked);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task StalledTransfer_EndsAtTheReadTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(100_000, seed: 20);
        var server = new ScriptedTransferServer(content) { StallAfter = 3000, StallIgnoresToken = true, ReadTimeout = TimeSpan.FromMilliseconds(300) };
        using var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);

        var (data, failure) = ReadAll(stream, new Random(21));

        AssertSameBytes(content.Take(3000).ToArray(), data);
        var exhausted = Assert.IsType<DatasusRetriesExhaustedException>(failure);
        Assert.IsType<TimeoutException>(exhausted.InnerException!.InnerException);
        await AssertEndsAsync(stream);
        Assert.Equal(1, server.Aborted);
        Assert.Equal(0, server.Active);
    }

    [Fact(Timeout = 30_000)]
    public async Task TokenCanceledBeforeOpening_Throws_AndHoldsNoConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeServer();
        server.Add(FilePath, Content(1000, seed: 22));
        using var pool = new ConnectionPool(server.Factory(), 1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(pool, FakeServer.Root, 1000, new CancellationToken(canceled: true)));

        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task MissingFile_ThrowsFileNotFound_AndKeepsTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeServer();
        using var pool = new ConnectionPool(server.Factory(), 1);

        await Assert.ThrowsAsync<FileNotFoundException>(() => Open(pool, FakeServer.Root, 1000, ct));

        Assert.Equal(1, server.ActiveConnections);
        using (await pool.AcquireAsync(ct))
        {
        }

        Assert.Equal(1, server.ConnectionsOpened);
    }

    [Fact(Timeout = 30_000)]
    public async Task OpeningFailsTransiently_ReportsRetriesExhaustedAfterOneAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new FakeServer();
        server.Add(FilePath, Content(1000, seed: 23));
        server.FailNextOperations(1, () => new TransientTransportException("421 Too many connections.", "421"));
        using var pool = new ConnectionPool(server.Factory(), 1);

        var exhausted = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => Open(pool, FakeServer.Root, 1000, ct));

        Assert.Equal("421", exhausted.FtpReplyCode);
        Assert.Equal(FilePath, exhausted.RemotePath);
        Assert.Equal(1, exhausted.Attempts);
        Assert.Equal(0, server.ActiveConnections);
    }

    // What happens today; whether a stream should outlive its pool is for the resumption to decide.
    [Fact(Timeout = 30_000)]
    public async Task PoolDisposedWhileTheStreamIsOpen_TheStreamReadsToTheEndAndClosesItsConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((2 * Capacity) + 55, seed: 24);
        var server = new FakeServer();
        server.Add(FilePath, content);
        var pool = new ConnectionPool(server.Factory(), 1);
        using var stream = await Open(pool, FakeServer.Root, content.Length, ct);

        pool.Dispose();

        var (data, failure) = ReadAll(stream, new Random(25));
        Assert.Null(failure);
        AssertSameBytes(content, data);
        await AssertEndsAsync(stream);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoStreamsOnAPoolOfTwo_ReadInTurns_AndAThirdLeaseWaitsUntilOneIsDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        // Larger than the buffer, so that each pump still holds its connection while it is read:
        // a connection is given back when its transfer ends, before the stream is disposed. Each
        // stream is read until it has 1 MiB, and no further, so that its pump, at most a buffer
        // ahead, has not received every byte when the third lease is asked for.
        var first = Content((3 * Capacity) + 11, seed: 26);
        var second = Content((3 * Capacity) + 22, seed: 27);
        var server = new FakeServer();
        server.Add(FilePath, first);
        server.Add("/X/B.dbc", second);
        using var pool = new ConnectionPool(server.Factory(), 2);
        var a = await Open(pool, FakeServer.Root, first.Length, ct);
        using var b = await RemoteReadAheadStream.OpenAsync(pool, NoRetries, RemotePath.Parse("/X/B.dbc", FakeServer.Root), Version(second.Length), ct);
        var readA = new MemoryStream();
        var readB = new MemoryStream();
        var buffer = new byte[50_000];
        var random = new Random(28);
        while (readA.Length < Capacity || readB.Length < Capacity)
        {
            if (readA.Length < Capacity)
            {
                readA.Write(buffer, 0, a.Read(buffer, 0, random.Next(1, buffer.Length)));
            }

            if (readB.Length < Capacity)
            {
                readB.Write(buffer, 0, b.Read(buffer, 0, random.Next(1, buffer.Length)));
            }
        }

        var third = pool.AcquireAsync(ct);
        await Task.Delay(200, ct);
        Assert.False(third.IsCompleted);

        a.Dispose();

        Assert.True(await Task.WhenAny(third, Task.Delay(TimeSpan.FromSeconds(5), ct)) == third, "The third lease did not come after a stream was disposed.");
        using (await third)
        {
        }

        int read;
        while ((read = b.Read(buffer, 0, random.Next(1, buffer.Length))) > 0)
        {
            readB.Write(buffer, 0, read);
        }

        AssertSameBytes(first.Take((int)readA.Length).ToArray(), readA.ToArray());
        AssertSameBytes(second, readB.ToArray());
        await AssertEndsAsync(a);
        await AssertEndsAsync(b);
    }

    // One attempt and no resumption: what these tests check is the stream around a single transfer.
    internal static RetryPolicy NoRetries { get; } = new(0, TimeSpan.Zero, (_, _) => Task.CompletedTask);

    internal static Task<RemoteReadAheadStream> Open(ConnectionPool pool, string root, long size, CancellationToken cancellationToken) =>
        RemoteReadAheadStream.OpenAsync(pool, NoRetries, RemotePath.Parse(FilePath, root), Version(size), cancellationToken);

    // The version the fakes announce for a file of that size: FakeServer.Time(0), which is also the
    // scripted server's default time and the loopback server's MDTM.
    internal static RemoteFileInfo Version(long size) => new(size, FakeServer.Time(0));

    internal static byte[] Content(int length, int seed)
    {
        var content = new byte[length];
        new Random(seed).NextBytes(content);
        return content;
    }

    // Reads to the end or to the first failure, in reads of random sizes.
    internal static (byte[] Data, Exception? Failure) ReadAll(Stream stream, Random random, int maxChunk = 200_000, Action? between = null)
    {
        var data = new MemoryStream();
        var buffer = new byte[maxChunk];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, random.Next(1, maxChunk + 1))) > 0)
            {
                data.Write(buffer, 0, read);
                between?.Invoke();
            }

            return (data.ToArray(), null);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return (data.ToArray(), exception);
        }
    }

    internal static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        for (var total = 0; total < count;)
        {
            var read = stream.Read(buffer, total, count - total);
            Assert.True(read > 0, $"The stream ended after {total} of {count} bytes.");
            total += read;
        }

        return buffer;
    }

    internal static void AssertSameBytes(byte[] expected, byte[] actual)
    {
        var length = Math.Min(expected.Length, actual.Length);
        for (var i = 0; i < length; i++)
        {
            if (expected[i] != actual[i])
            {
                Assert.Fail($"The bytes differ at offset {i} of {expected.Length}.");
            }
        }

        Assert.Equal(expected.Length, actual.Length);
    }

    internal static async Task AssertEndsAsync(RemoteReadAheadStream stream)
    {
        var ended = await Task.WhenAny(stream.Completion, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        Assert.True(ended == stream.Completion, "The pump did not end within 10 seconds.");
        await stream.Completion;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(5, CancellationToken.None);
        }
    }
}
