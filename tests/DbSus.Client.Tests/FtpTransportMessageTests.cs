using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The real FTP transport (FluentFTP) against a loopback server whose data connections always fail
// (refused, or timed out where the system is slow to refuse): what the messages say, and which
// passive commands FluentFTP sends. They run alone (no other test in parallel): the ports they
// rely on being closed are released ephemeral ports, which another test's listener could take.
[Collection(nameof(RunAlone))]
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
    public async Task ListingDataConnectionFails_SaysListingAndAPossibleCause()
    {
        using var client = Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.StartsWith("Listing /X failed after 1 attempt: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains($"while listing /dissemin/publicos/X on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("The control connection was not reopened during this step; a possible cause is a passive data connection being blocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PASV", _server.Commands);
        Assert.DoesNotContain("EPSV", _server.Commands);
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

    // The server lists EPSV and refuses it: the client never asks for it (PASVEX sends PASV), so it
    // does not depend on how FluentFTP falls back from EPSV (FluentFTP 55.1.0 stopped falling back
    // to PASV when FEAT lists EPSV).
    [Fact]
    public async Task EpsvListedAndRefused_IsNeverSent()
    {
        _server.RefuseEpsv = true;

        using var client = Client();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

        Assert.Equal(["PASV"], _server.Commands.Where(command => command is "EPSV" or "PASV"));
    }

    [Fact]
    // Failed once, in a full run, without a message to go on; on failure it now says when it
    // started and what the port announced as closed was doing.
    public async Task ReadingDataConnectionFails_SaysOpeningForReading()
    {
        var started = DateTime.UtcNow;
        using var client = Client();

        try
        {
            var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.DownloadAsync("/X/A.dbc", Ct));

            Assert.StartsWith("Downloading /X/A.dbc failed after 1 attempt: ", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"while opening /dissemin/publicos/X/A.dbc for reading on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
            Assert.Contains("a possible cause is a passive data connection being blocked", exception.Message, StringComparison.Ordinal);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            throw new Xunit.Sdk.XunitException($"{failure.Message}{Environment.NewLine}Test started at {started:yyyy-MM-dd HH:mm:ss.fff} UTC; control port {_server.Port}. {await DescribeClosedPortAsync(_server.PassivePort)}", failure);
        }
    }

    // What a new connection to a port that should be closed does now: refused, timed out or connected.
    private static async Task<string> DescribeClosedPortAsync(int port, string name = "the passive port announced as closed")
    {
        var at = DateTime.UtcNow;
        using var probe = new System.Net.Sockets.TcpClient();
        var connect = probe.ConnectAsync(System.Net.IPAddress.Loopback, port);
        var outcome = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)) != connect
            ? "timed out after 2 s"
            : connect.IsFaulted
                ? $"was refused ({(connect.Exception?.InnerException as System.Net.Sockets.SocketException)?.SocketErrorCode})"
                : "connected: something is listening on it";
        return $"Diagnostics at {at:yyyy-MM-dd HH:mm:ss.fff} UTC: {name} was {port}; a new connection to it {outcome}.";
    }

    [Fact]
    // Failed once, in a full run; on failure it says when it started, what the server saw, and
    // what the control port (stopped after login) and the passive port were doing.
    public async Task ControlConnectionReopenedDuringTheStep_DoesNotSingleOutTheDataConnection()
    {
        _server.DropControlConnectionAfterLogin = true;
        var started = DateTime.UtcNow;

        using var client = Client();

        try
        {
            var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", Ct));

            Assert.Contains($"while listing /dissemin/publicos/X on 127.0.0.1:{_server.Port}: ", exception.Message, StringComparison.Ordinal);
            Assert.Contains("The control connection was reopened during this step, so the connection attempt that failed may be the control connection or the passive data connection", exception.Message, StringComparison.Ordinal);
        }
        catch (Xunit.Sdk.XunitException failure)
        {
            throw new Xunit.Sdk.XunitException(
                $"{failure.Message}{Environment.NewLine}Test started at {started:yyyy-MM-dd HH:mm:ss.fff} UTC; control connections accepted: {_server.ControlConnections}; commands: [{string.Join(",", _server.Commands)}]."
                + $" {await DescribeClosedPortAsync(_server.Port, "the control port, stopped after login,")} {await DescribeClosedPortAsync(_server.PassivePort)}",
                failure);
        }
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
