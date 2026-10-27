using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// Opening a transfer that resumes, from an offset beyond 0, has a limit of its own, from the PASV
// to the reply to RETR: past it the connection is closed and the attempt is a transient failure.
// The caller's cancellation stays a cancellation, and an opening from byte 0 keeps the read
// timeout alone. Against the loopback server: on .NET Framework through the synchronous FluentFTP
// client, elsewhere through the asynchronous one.
public sealed class ResumeOpenTimeoutTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbc";
    private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(300);
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly byte[] _content = Content(200_000, seed: 91);

    public ResumeOpenTimeoutTests() => _server.Files[FullPath] = _content;

    public void Dispose() => _server.Dispose();

    [Fact(Timeout = 60_000)]
    public async Task RestUnanswered_TheOpeningEndsAtItsLimit_AsATransientFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "REST";
        using var connection = await Factory(ShortLimit).ConnectAsync(ct);
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAsync<TransientTransportException>(() => connection.OpenReadAsync(FullPath, 1000, ct));

        // Far from the read timeout of 30 s.
        Assert.InRange(watch.Elapsed, ShortLimit - TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(10));
        Assert.True(failure.IsExpiredLimit);
        Assert.Null(failure.ReplyCode);
        Assert.Contains("did not open the resumed transfer within 0.3 s, so the connection was closed", failure.Message, StringComparison.Ordinal);
        Assert.True(connection.IsBroken);
        await WaitUntilAsync(() => _server.ControlConnectionsClosedByClient == 1, "the client to close the control connection");
        Assert.Single(_server.Commands, command => command == "REST");
    }

    [Fact(Timeout = 60_000)]
    public async Task SlowOpening_WithinTheLimit_ResumesFromTheOffset()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.DelayedCommand = "REST";
        _server.ReplyDelay = TimeSpan.FromMilliseconds(200);
        using var connection = await Factory(TimeSpan.FromSeconds(5)).ConnectAsync(ct);

        var data = await ReadToEndAsync(connection, 1000, ct);

        AssertSameBytes(_content.Skip(1000).ToArray(), data);
        Assert.False(connection.IsBroken);
        Assert.Equal(new long[] { 1000 }, _server.Restarts);
    }

    [Fact(Timeout = 60_000)]
    public async Task SlowPassiveReply_LimitsOnlyAnOpeningBeyondByteZero()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.DelayedCommand = "PASV";
        _server.ReplyDelay = TimeSpan.FromMilliseconds(900);

        // From the start: the read timeout alone, so the slow PASV is waited for.
        using (var connection = await Factory(ShortLimit).ConnectAsync(ct))
        {
            AssertSameBytes(_content, await ReadToEndAsync(connection, 0, ct));
        }

        // From an offset: the limit covers the PASV too, before any REST is sent.
        using (var connection = await Factory(ShortLimit).ConnectAsync(ct))
        {
            var failure = await Assert.ThrowsAsync<TransientTransportException>(() => connection.OpenReadAsync(FullPath, 1000, ct));
            Assert.True(failure.IsExpiredLimit);
        }

        Assert.Empty(_server.Restarts);
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerCanceledDuringTheOpening_IsACancellation_NotAnExpiredLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "REST";
        using var connection = await Factory(TimeSpan.FromSeconds(20)).ConnectAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.OpenReadAsync(FullPath, 1000, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(failure.InnerException is TransientTransportException { IsExpiredLimit: true });
        Assert.True(connection.IsBroken);
    }

    [Fact(Timeout = 60_000)]
    public async Task ExpiredOpenings_AreRetriedOnNewConnections_WithOnRetry()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "REST";
        var infos = new List<DatasusRetryInfo>();
        var retry = new RetryPolicy(2, TimeSpan.Zero, (_, _) => Task.CompletedTask, infos.Add);
        using var pool = new ConnectionPool(Factory(ShortLimit), 1);

        var failure = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => retry.RunAsync("Reading A.dbc", "/X/A.dbc", async token =>
        {
            using var lease = await pool.AcquireAsync(token);
            using var stream = await lease.Connection.OpenReadAsync(FullPath, 1000, token);
            lease.Keep();
            return true;
        }, ct));

        Assert.Equal(3, failure.Attempts);
        Assert.Contains("did not open the resumed transfer", failure.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2], infos.Select(info => info.Attempt));
        Assert.Equal(3, _server.ControlConnections);
        Assert.Equal(3, _server.Commands.Count(command => command == "REST"));
    }

    [Fact(Timeout = 60_000)]
    public async Task CallerCanceledDuringTheOpening_IsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "REST";
        var infos = new List<DatasusRetryInfo>();
        var retry = new RetryPolicy(3, TimeSpan.Zero, (_, _) => Task.CompletedTask, infos.Add);
        using var pool = new ConnectionPool(Factory(TimeSpan.FromSeconds(20)), 1);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.RunAsync("Reading A.dbc", "/X/A.dbc", async token =>
        {
            using var lease = await pool.AcquireAsync(token);
            using var stream = await lease.Connection.OpenReadAsync(FullPath, 1000, token);
            return true;
        }, cancellation.Token));

        Assert.Empty(infos);
        Assert.Equal(1, _server.ControlConnections);
    }

    private static async Task<byte[]> ReadToEndAsync(IDatasusConnection connection, long offset, CancellationToken cancellationToken)
    {
        using var stream = await connection.OpenReadAsync(FullPath, offset, cancellationToken);
        using var data = new MemoryStream();
        await stream.CopyToAsync(data, 81920, cancellationToken);
        return data.ToArray();
    }

    private FtpConnectionFactory Factory(TimeSpan resumeOpenTimeout) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadTimeout = TimeSpan.FromSeconds(30),
        ResumeOpenTimeout = resumeOpenTimeout,
    });
}
