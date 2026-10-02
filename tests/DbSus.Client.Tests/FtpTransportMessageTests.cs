using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The real FTP transport (FluentFTP) against a loopback server whose data connections always fail
// (refused, or timed out where the system is slow to refuse): what the messages say, and which
// passive commands FluentFTP sends.
public sealed class FtpTransportMessageTests : IDisposable
{
    private readonly LoopbackFtpServer _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ControlConnectionFails_SaysConnectingWithHostAndPort()
    {
        var port = LoopbackFtpServer.ClosedPort();

        using var client = Client(port);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.Contains($"while connecting to 127.0.0.1:{port}: ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("passive", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingDataConnectionFails_WithEpsv_SaysListingAndAPossibleCause()
    {
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.StartsWith("Listing /X failed after 1 attempt: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"while listing /dissemin/publicos/X on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The control connection was not reopened during this step; a possible cause is a passive data connection being blocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("EPSV", _server.Commands);
        Assert.DoesNotContain("PASV", _server.Commands);
    }

    [Fact]
    public async Task ListingDataConnectionFails_WithoutEpsv_UsesPasv()
    {
        _server.AdvertiseEpsv = false;

        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.Contains("a possible cause is a passive data connection being blocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PASV", _server.Commands);
        Assert.DoesNotContain("EPSV", _server.Commands);
    }

    [Fact]
    public async Task EpsvRefusedWithAReply_FallsBackToPasv()
    {
        _server.RefuseEpsv = true;

        using var client = Client();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.Equal(["EPSV", "PASV"], _server.Commands.Where(command => command is "EPSV" or "PASV"));
    }

    [Fact]
    public async Task ReadingDataConnectionFails_SaysOpeningForReading()
    {
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.DownloadAsync("/X/A.dbc", Ct));

        Assert.StartsWith("Downloading /X/A.dbc failed after 1 attempt: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"while opening /dissemin/publicos/X/A.dbc for reading on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("a possible cause is a passive data connection being blocked", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ControlConnectionReopenedDuringTheStep_DoesNotSingleOutTheDataConnection()
    {
        _server.DropControlConnectionAfterLogin = true;

        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.Contains($"while listing /dissemin/publicos/X on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The control connection was reopened during this step, so the connection attempt that failed may be the control connection or the passive data connection", exception.Message, StringComparison.Ordinal);
    }

    private DatasusClient Client(int? port = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = port ?? _server.Port,
        MaxRetries = 0,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = TimeSpan.FromSeconds(5),
    });
}
