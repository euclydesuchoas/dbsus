using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using FluentFTP;
using Xunit;

namespace DbSus.Client.Tests;

// On .NET Framework the synchronous session needs FluentFTP to call its connection hooks: without
// them, cancelling would not end a stalled operation. The session checks that each hook did its
// part and refuses to go on otherwise. The hooks are switched off here through an internal,
// test-only setting.
public sealed class HookCheckTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbf";
    private readonly LoopbackFtpServer _server = new()
    {
        AdvertiseEpsv = false,
        Listing = [LoopbackFtpServer.FileLine("A.dbf", 1000)],
    };

    public HookCheckTests()
    {
        _server.Files[FullPath] = new byte[1000];
    }

    public void Dispose() => _server.Dispose();

    [Fact(Timeout = 30_000)]
    public async Task WithBothHooks_ConnectsListsAndOpens()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session(SocketHooks.None);

        await session.ConnectAsync(ct);
        var items = await session.GetListingAsync("/dissemin/publicos/X", FtpListOption.ForceList, ct);
        using var stream = await session.OpenReadAsync(FullPath, 0, ct);

        Assert.Single(items);
        Assert.NotNull(stream);
    }

    [Fact(Timeout = 30_000)]
    public async Task WithoutTheControlHook_ConnectingFails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session(SocketHooks.Control);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => session.ConnectAsync(ct));

        AssertNamesBothVersions(exception, "control");
    }

    [Fact(Timeout = 30_000)]
    public async Task WithoutTheDataHook_OpeningForReadingFailsBeforeAnyRead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session(SocketHooks.Data);
        await session.ConnectAsync(ct);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => session.OpenReadAsync(FullPath, 0, ct));

        AssertNamesBothVersions(exception, "data");
    }

    [Fact(Timeout = 30_000)]
    public async Task WithoutTheDataHook_ListingFails()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = Session(SocketHooks.Data);
        await session.ConnectAsync(ct);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => session.GetListingAsync("/dissemin/publicos/X", FtpListOption.ForceList, ct));

        AssertNamesBothVersions(exception, "data");
    }

    [Fact]
    public async Task NotSupported_IsNotRetried()
    {
        var policy = new RetryPolicy(4, TimeSpan.Zero, (_, _) => Task.CompletedTask);
        var calls = 0;

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            policy.RunAsync<int>("op", "/x", _ => { calls++; throw new NotSupportedException("unsupported FluentFTP"); }, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    private static void AssertNamesBothVersions(NotSupportedException exception, string connection)
    {
        var inUse = typeof(FtpClient).Assembly.GetName().Version!.ToString(3);
        Assert.Contains($"FluentFTP {inUse}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"tested with FluentFTP {SyncFtpSession.TestedFluentFtpVersion}", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"its {connection} connection", exception.Message, StringComparison.Ordinal);
    }

    private SyncFtpSession Session(SocketHooks disabledForTest) => new(
        "127.0.0.1",
        _server.Port,
        new FtpConfig { DataConnectionType = FtpDataConnectionType.AutoPassive, ConnectTimeout = 2000, ReadTimeout = 5000, DataConnectionReadTimeout = 5000 },
        disabledForTest);
}
