using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using FluentFTP;
using Xunit;

namespace DbSus.Client.Tests;

// Cancelling an operation relies on FluentFTP calling its protected virtual connection hooks, which
// is how a session learns its control and data sockets. If a FluentFTP version stopped calling one
// of them, nothing would fail to build or load: cancellation would just stop working on .NET
// Framework. This test watches the sockets from the server's side, for both kinds of session.
public sealed class SessionAbortTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbf";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false, StallTransferAfter = 0 };

    public SessionAbortTests()
    {
        _server.Files[FullPath] = new byte[1000];
    }

    public void Dispose() => _server.Dispose();

    // Delayed: the client closes the sockets before the server touches the data connection.
    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Abort_ClosesTheControlAndDataSocketsAndEndsThePendingRead(bool synchronous, bool delayed)
    {
        var ct = TestContext.Current.CancellationToken;
        _server.DelayBeforeStalledData = delayed ? TimeSpan.FromMilliseconds(200) : TimeSpan.Zero;
        var config = new FtpConfig { DataConnectionType = FtpDataConnectionType.AutoPassive, ConnectTimeout = 2000, ReadTimeout = 60_000, DataConnectionReadTimeout = 60_000 };
        using var session = synchronous
            ? new SyncFtpSession("127.0.0.1", _server.Port, config)
            : (FtpSession)new AsyncFtpSession("127.0.0.1", _server.Port, config);
        await session.ConnectAsync(ct);
        using var stream = await session.OpenReadAsync(FullPath, 0, ct);
        var read = stream.ReadAsync(new byte[16], 0, 16, ct);

        session.Abort();

        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), ct)));
        await Assert.ThrowsAnyAsync<Exception>(() => read);
        await WaitUntil(() => _server.ControlConnectionsClosedByClient == 1 && _server.DataConnectionsClosedByClient == 1, ct);
    }

    private async Task WaitUntil(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50, ct);
        }

        Assert.True(condition(), $"Closed by the client: control {_server.ControlConnectionsClosedByClient}, data {_server.DataConnectionsClosedByClient} (expected 1 and 1).");
    }
}
