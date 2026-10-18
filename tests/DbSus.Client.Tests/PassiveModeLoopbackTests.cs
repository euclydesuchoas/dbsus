using System.Net;
using System.Net.Sockets;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using FluentFTP;
using Xunit;

namespace DbSus.Client.Tests;

// The data connection mode (PASVEX) over FluentFTP and real sockets: the client sends PASV, never
// EPSV over IPv4, and connects to Host whatever address the PASV reply names; over IPv6 FluentFTP
// sends EPSV in its place. The loopback server awaits each data connection on 127.0.0.1 and on
// 127.0.0.2, so a client that went to the named address would be seen.
public sealed class PassiveModeLoopbackTests : IDisposable
{
    private const string FilePath = "/X/A.dbf";
    private const string FullPath = "/dissemin/publicos/X/A.dbf";
    private readonly LoopbackFtpServer _server = new();
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "dbsus-passive-tests", Guid.NewGuid().ToString("N"));

    public PassiveModeLoopbackTests()
    {
        _server.Listing = [LoopbackFtpServer.FileLine("A.dbf", 1000)];
        _server.Files[FullPath] = [.. Enumerable.Range(0, 1000).Select(i => (byte)(i % 251))];
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Config_UsesPasvex()
    {
        var config = FtpConnectionFactory.CreateConfig(new DatasusClientOptions());

        Assert.Equal(FtpDataConnectionType.PASVEX, config.DataConnectionType);
    }

    // EPSV listed and working: the client still sends PASV, and the data goes to the host.
    [Fact(Timeout = 30_000)]
    public async Task EpsvListedAndWorking_PasvIsSent_AndTheDataGoesToTheHost()
    {
        using var client = Client();

        var entries = await client.ListAsync("/X", TestContext.Current.CancellationToken);
        var file = await client.DownloadAsync(FilePath, TestContext.Current.CancellationToken);

        Assert.Equal("A.dbf", Assert.Single(entries).Name);
        Assert.Equal(_server.Files[FullPath], File.ReadAllBytes(file));
        Assert.Equal(["PASV", "PASV"], _server.Commands.Where(command => command is "EPSV" or "PASV"));
        Assert.Equal(["127.0.0.1", "127.0.0.1"], _server.DataArrivals);
    }

    // A PASV reply that names another address (as a hostile or misconfigured server could): the
    // listing and the download still go to the host of the control connection.
    [Fact(Timeout = 30_000)]
    public async Task PasvNamesAnotherAddress_TheDataGoesToTheHost()
    {
        _server.AdvertiseEpsv = false;
        _server.PassiveAddress = "127,0,0,2";
        using var client = Client();

        var entries = await client.ListAsync("/X", TestContext.Current.CancellationToken);
        var file = await client.DownloadAsync(FilePath, TestContext.Current.CancellationToken);

        Assert.Single(entries);
        Assert.Equal(_server.Files[FullPath], File.ReadAllBytes(file));
        Assert.Equal(["127.0.0.1", "127.0.0.1"], _server.DataArrivals);
    }

    // Over IPv6 PASV cannot work; FluentFTP sends EPSV in place of PASVEX.
    [Fact(Timeout = 30_000)]
    public async Task Ipv6ControlConnection_EpsvIsSent()
    {
        LoopbackFtpServer server;
        try
        {
            server = new LoopbackFtpServer(IPAddress.IPv6Loopback);
        }
        catch (SocketException exception)
        {
            Assert.Skip($"No IPv6 loopback here ({exception.SocketErrorCode}).");
            return;
        }

        using (server)
        {
            server.Listing = [LoopbackFtpServer.FileLine("A.dbf", 1000)];
            using var client = Client(host: "::1", port: server.Port);

            var entries = await client.ListAsync("/X", TestContext.Current.CancellationToken);

            Assert.Single(entries);
            Assert.Equal(["EPSV"], server.Commands.Where(command => command is "EPSV" or "PASV"));
            Assert.Equal(["::1"], server.DataArrivals);
        }
    }

    // A refused PASV is handled as any other refusal of the step: the type and the message are the
    // ones the client gives for that reply, with the text of the server escaped.
    [Fact(Timeout = 30_000)]
    public async Task PasvRefused_ListingAndDownloadAreRefused()
    {
        _server.PasvRefusal = "500 PASV not\u001b[31m understood.";
        using var client = Client();

        var listing = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));
        var download = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.DownloadAsync(FilePath, TestContext.Current.CancellationToken));

        TestContext.Current.TestOutputHelper?.WriteLine($"listing: {listing.Message}{Environment.NewLine}download: {download.Message}");
        Assert.Equal("500", listing.FtpReplyCode);
        Assert.Contains("PASV not\\x1B[31m understood.", listing.Message, StringComparison.Ordinal);
        Assert.Contains("PASV not\\x1B[31m understood.", download.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", listing.Message + download.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("EPSV", _server.Commands);
    }

    // A 421 to PASV, the server closing the control connection: a transient failure, retried; the
    // test reports which exception FluentFTP raised (its own, or one it is not meant to throw).
    [Fact(Timeout = 30_000)]
    public async Task PasvAnsweredWith421AndClosed_IsTransient()
    {
        _server.PasvRefusal = "421 Service not available, closing control connection.";
        _server.CloseAfterPasvRefusal = true;
        using var client = Client(retries: 1);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        var transient = Assert.IsType<TransientTransportException>(exception.InnerException);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{exception.Message}{Environment.NewLine}library failure: {transient.IsLibraryFailure}; original: {transient.InnerException?.GetType().FullName}");
        Assert.Equal(2, exception.Attempts);
        Assert.Equal(2, _server.ControlConnections);
        Assert.DoesNotContain("EPSV", _server.Commands);
    }

    private DatasusClient Client(string host = "127.0.0.1", int? port = null, int retries = 0) => new(new DatasusClientOptions
    {
        Host = host,
        Port = port ?? _server.Port,
        MaxRetries = retries,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReadTimeout = TimeSpan.FromSeconds(5),
        CacheDirectory = _cache,
    });
}
