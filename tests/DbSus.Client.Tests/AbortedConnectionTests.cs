using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using FluentFTP;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// After an abort, a connection opens no other. FluentFTP reconnects by itself when it finds the
// control connection closed, and so it would after an abort, on a socket that the abort can no
// longer close. A retry still gets a new connection, and a reconnection of a connection that was
// not aborted still works. Against the loopback server: on .NET Framework through the synchronous
// FluentFTP client, elsewhere through the asynchronous one; the session tests use both on both.
public sealed class AbortedConnectionTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbc";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly byte[] _content = Content(200_000, seed: 91);

    public AbortedConnectionTests()
    {
        _server.Files[FullPath] = _content;
        _server.Listing = [LoopbackFtpServer.FileLine("A.dbc", _content.Length)];
    }

    public void Dispose() => _server.Dispose();

    [Theory(Timeout = 60_000)]
    [InlineData("GetInfo")]
    [InlineData("List")]
    [InlineData("OpenRead")]
    [InlineData("OpenReadFromAnOffset")]
    public async Task AfterAnAbort_TheNextOperationFails_WithoutOpeningAConnection(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = (FtpConnection)await Factory(TestTimes.Read).ConnectAsync(ct);
        var mark = _server.Commands.Count;

        connection.Abort();
        await Assert.ThrowsAsync<TransientTransportException>(() => Run(connection, operation, ct));
        connection.Dispose();

        await WaitUntilAsync(() => _server.OpenControlConnections == 0, "the server to see the control connection closed");
        Assert.True(connection.IsBroken);
        Assert.Equal(1, _server.ControlConnections);
        Assert.Empty(_server.Commands.Skip(mark));
    }

    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAnAbort_ASessionCommandFails_WithoutOpeningAConnection(bool synchronous)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new FtpConfig { DataConnectionType = FtpDataConnectionType.PASVEX, ConnectTimeout = (int)TestTimes.Connect.TotalMilliseconds, ReadTimeout = (int)TestTimes.Read.TotalMilliseconds };
        using var session = synchronous
            ? new SyncFtpSession("127.0.0.1", _server.Port, config)
            : (FtpSession)new AsyncFtpSession("127.0.0.1", _server.Port, config);
        await session.ConnectAsync(ct);
        var mark = _server.Commands.Count;

        session.Abort();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteAsync("SIZE " + FullPath, ct));

        Assert.Contains(Chain(failure), exception => exception is IOException && exception.Message.Contains("closed by force", StringComparison.Ordinal));
        Assert.Equal(1, _server.ControlConnections);
        Assert.Empty(_server.Commands.Skip(mark));
    }

    // The limit of a resumed opening ran out: the next attempt connects anew and resumes.
    [Fact(Timeout = 60_000)]
    public async Task AfterAnExpiredLimit_TheRetryOpensANewConnection_AndResumes()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "REST";
        var retry = new RetryPolicy(2, TimeSpan.Zero, (_, _) => Task.CompletedTask, _ => _server.SilentOn = null);
        using var pool = new ConnectionPool(Factory(TimeSpan.FromSeconds(3)), 1);

        var data = await retry.RunAsync("Reading A.dbc", "/X/A.dbc", async token =>
        {
            using var lease = await pool.AcquireAsync(token);
            using var stream = await lease.Connection.OpenReadAsync(FullPath, 1000, token);
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, 81920, token);
            lease.Keep();
            return copy.ToArray();
        }, ct);

        // The REST left unanswered is not among the restarts the server made.
        AssertSameBytes(_content.Skip(1000).ToArray(), data);
        Assert.Equal(2, _server.ControlConnections);
        Assert.Equal(2, _server.Commands.Count(command => command == "REST"));
        Assert.Equal([1000L], _server.Restarts);
    }

    // FluentFTP reconnects by itself, within the opening of a resumed transfer, when it finds the
    // control connection closed: here, by the 421 the server left there. The opening's limit covers
    // that reconnection: with the login held by the server, the opening ends at the limit, before
    // any PASV or REST.
    [Fact(Timeout = 60_000)]
    public async Task ResumedOpening_ItsLimitCoversAReconnection()
    {
        var ct = TestContext.Current.CancellationToken;
        var limit = TimeSpan.FromSeconds(2);
        using var connection = await ConnectThenLoseTheControlConnectionAsync(limit, ct);
        _server.DelayedCommand = "USER";
        _server.ReplyDelay = TestTimes.Apart(limit, TimeSpan.FromSeconds(15));
        var mark = _server.Commands.Count;
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<TransientTransportException>(() => connection.OpenReadAsync(FullPath, 1000, ct));

        Assert.True(failure.IsExpiredLimit, failure.Message);
        Assert.InRange(watch.Elapsed, limit - TimeSpan.FromMilliseconds(50), _server.ReplyDelay);
        Assert.Equal(2, _server.ControlConnections);
        var after = _server.Commands.Skip(mark).ToList();
        Assert.Contains("USER", after);
        Assert.DoesNotContain(after, command => command is "PASV" or "REST" or "RETR");
        Assert.Empty(_server.Restarts);
    }

    // The same reconnection, of a connection that was not aborted, within the limit: the transfer
    // resumes from the offset on the new control connection.
    [Fact(Timeout = 60_000)]
    public async Task ResumedOpening_AReconnectionWithinTheLimit_Resumes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await ConnectThenLoseTheControlConnectionAsync(TimeSpan.FromSeconds(5), ct);

        using var stream = await connection.OpenReadAsync(FullPath, 1000, ct);
        using var data = new MemoryStream();
        await stream.CopyToAsync(data, 81920, ct);

        AssertSameBytes(_content.Skip(1000).ToArray(), data.ToArray());
        Assert.Equal(2, _server.ControlConnections);
        Assert.Equal([1000L], _server.Restarts);
    }

    private static Task Run(FtpConnection connection, string operation, CancellationToken ct) => operation switch
    {
        "GetInfo" => connection.GetInfoAsync(FullPath, ct),
        "List" => connection.ListAsync("/dissemin/publicos/X", ct),
        "OpenRead" => OpenAndDisposeAsync(connection, 0, ct),
        "OpenReadFromAnOffset" => OpenAndDisposeAsync(connection, 1000, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static async Task OpenAndDisposeAsync(FtpConnection connection, long offset, CancellationToken ct)
    {
        using var stream = await connection.OpenReadAsync(FullPath, offset, ct);
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    // A connection whose control connection the server closes right after the login, leaving a 421
    // for the client to find before its next command.
    private async Task<IDatasusConnection> ConnectThenLoseTheControlConnectionAsync(TimeSpan resumeOpenTimeout, CancellationToken ct)
    {
        _server.CloseControlConnectionSilentlyAfter = "TYPE";
        _server.CloseNotice = "421 Closing the control connection.";
        var connection = await Factory(resumeOpenTimeout).ConnectAsync(ct);
        await _server.ControlConnectionClosedSilently;

        // On the loopback interface the notice and the end of the connection reach the client at
        // once; the wait is far beyond that, and nothing races it.
        await Task.Delay(200, ct);
        return connection;
    }

    private FtpConnectionFactory Factory(TimeSpan resumeOpenTimeout)
    {
        var options = TestTimes.Loopback(_server.Port);
        options.ResumeOpenTimeout = resumeOpenTimeout;
        return new FtpConnectionFactory(options);
    }
}
