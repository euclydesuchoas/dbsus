using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// The read-ahead stream over the real transport (FluentFTP, a passive data connection, REST and
// the final 226), against the loopback server: on .NET Framework through the synchronous FluentFTP
// client, elsewhere through the asynchronous one.
public sealed class ReadAheadStreamLoopbackTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbc";
    private const int Capacity = RemoteReadAheadStream.Capacity;
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    [Fact(Timeout = 60_000)]
    public async Task ReadsTheFileByteForByte_AndReusesTheConnectionAfterTheFinalReply()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content((3 * Capacity) + 321, seed: 31);
        _server.Files[FullPath] = content;
        using var pool = Pool(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 2; i++)
        {
            using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);
            var (data, failure) = ReadAll(stream, new Random(32 + i));
            Assert.Null(failure);
            AssertSameBytes(content, data);
            await AssertEndsAsync(stream);
        }

        Assert.Equal(1, _server.ControlConnections);
        Assert.Equal(2, _server.Commands.Count(command => command == "RETR"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerSendsMore_TheExcessIsNeverDelivered_AndTheConnectionIsNotReused()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(Capacity + 4321, seed: 33);
        _server.Files[FullPath] = content;
        _server.ExtraBytesAfterFile = 50_000;
        using var pool = Pool(TimeSpan.FromSeconds(30));

        using (var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct))
        {
            var (data, failure) = ReadAll(stream, new Random(34));
            AssertSameBytes(content, data);
            Assert.Contains("sent more than", Assert.IsType<DatasusRetriesExhaustedException>(failure).Message, StringComparison.Ordinal);
            await AssertEndsAsync(stream);
        }

        _server.ExtraBytesAfterFile = null;
        using (var again = await Open(pool, ScriptedTransferServer.Root, content.Length, ct))
        {
            AssertSameBytes(content, ReadAll(again, new Random(35)).Data);
        }

        Assert.Equal(2, _server.ControlConnections);
    }

    [Fact(Timeout = 60_000)]
    public async Task TransferEndsBeforeTheSize_Fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(300_000, seed: 36);
        _server.Files[FullPath] = content;
        using var pool = Pool(TimeSpan.FromSeconds(30));
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length + 10, ct);

        var (data, failure) = ReadAll(stream, new Random(37));

        AssertSameBytes(content, data);
        Assert.Contains($"Received {content.Length} of {content.Length + 10} bytes", Assert.IsType<DatasusRetriesExhaustedException>(failure).Message, StringComparison.Ordinal);
        await AssertEndsAsync(stream);
    }

    [Fact(Timeout = 60_000)]
    public async Task Canceled_WhileTheReadWaitsForAStalledServer()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(400_000, seed: 38);
        _server.Files[FullPath] = content;
        _server.StallTransferAfter = 100_000;
        using var pool = Pool(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, cancellation.Token);
        AssertSameBytes(content.Take(100_000).ToArray(), ReadExactly(stream, 100_000));
        var blocked = Task.Run(() => stream.Read(new byte[100], 0, 100), ct);
        await Task.Delay(200, ct);
        Assert.False(blocked.IsCompleted);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        await AssertEndsAsync(stream);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        await WaitUntilAsync(() => _server.DataConnectionsClosedByClient == 1, "the client to close the stalled data connection");
    }

    [Fact(Timeout = 60_000)]
    public async Task StalledServer_EndsAtTheReadTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(400_000, seed: 39);
        _server.Files[FullPath] = content;
        _server.StallTransferAfter = 50_000;
        using var pool = Pool(TimeSpan.FromSeconds(1));
        using var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, ct);

        var (data, failure) = ReadAll(stream, new Random(40));

        AssertSameBytes(content.Take(50_000).ToArray(), data);
        Assert.IsType<TimeoutException>(Assert.IsType<DatasusRetriesExhaustedException>(failure).InnerException!.InnerException);
        await AssertEndsAsync(stream);
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedWhileThePumpWaitsForRoom_FreesThePlaceInThePool(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(6 * Capacity, seed: 41);
        _server.Files[FullPath] = content;
        using var pool = Pool(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stream = await Open(pool, ScriptedTransferServer.Root, content.Length, cancellation.Token);
        await WaitUntilAsync(() => stream.MaxBuffered == Capacity, "the pump to fill the buffer");

        if (cancel)
        {
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => stream.Read(new byte[10], 0, 10));
        }

        stream.Dispose();

        await AssertEndsAsync(stream);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using (await pool.AcquireAsync(timeout.Token))
        {
        }

        Assert.Equal(2, _server.ControlConnections);
    }

    private ConnectionPool Pool(TimeSpan readTimeout) => new(
        new FtpConnectionFactory(new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = readTimeout,
        }),
        1);
}
