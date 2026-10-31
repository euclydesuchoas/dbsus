using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// What reaches the FTP server as a path: a RootPath is checked when the client is created, a path
// that the FTP library refuses is not retried, and no command ever carries a line break or a NUL.
public sealed class FtpPathSafetyTests : IDisposable
{
    private const string FullPath = "/dissemin/publicos/X/A.dbc";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public FtpPathSafetyTests() => _server.Files[FullPath] = new byte[1000];

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData("/dissemin/publicos\r\nDELE /dissemin/publicos/X/A.dbc", "control characters")]
    [InlineData("/dissemin//publicos", "empty segment")]
    [InlineData("/dissemin/publicos%", "'%' and ';'")]
    [InlineData("dissemin/publicos", "must start with '/'")]
    public void Client_RootPathNotAcceptable_ThrowsArgumentExceptionOnCreation(string root, string reason)
    {
        var options = TestTimes.Loopback(_server.Port);
        options.RootPath = root;

        var exception = Assert.Throws<ArgumentException>(() => new DatasusClient(options));

        Assert.Equal("RootPath", exception.ParamName);
        Assert.Contains(reason, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, _server.ControlConnections);
    }

    // FluentFTP refuses '%' in the path of RETR (FtpSanitizeException); that answer would be the
    // same on every attempt, so the retry policy lets it through at once.
    [Fact(Timeout = 60_000)]
    public async Task PathTheFtpLibraryRefuses_IsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var retries = new List<DatasusRetryInfo>();
        var retry = new RetryPolicy(3, TimeSpan.Zero, (_, _) => Task.CompletedTask, retries.Add);
        using var pool = new ConnectionPool(new FtpConnectionFactory(TestTimes.Loopback(_server.Port)), 1);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => retry.RunAsync("Reading a%41.dbc", "/X/a%41.dbc", async token =>
        {
            using var lease = await pool.AcquireAsync(token);
            using var stream = await lease.Connection.OpenReadAsync("/dissemin/publicos/X/a%41.dbc", 0, token);
            return true;
        }, ct));

        Assert.Contains("The FTP library refused the path", exception.Message, StringComparison.Ordinal);
        Assert.Empty(retries);
        Assert.DoesNotContain("RETR", _server.Commands);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("GetInfo")]
    [InlineData("List")]
    [InlineData("OpenRead")]
    public async Task PathWithALineBreak_IsNeverSent(string operation)
    {
        var ct = TestContext.Current.CancellationToken;
        using var connection = await new FtpConnectionFactory(TestTimes.Loopback(_server.Port)).ConnectAsync(ct);
        var mark = _server.Commands.Count;
        const string Injected = FullPath + "\r\nDELE " + FullPath;

        await Assert.ThrowsAsync<ArgumentException>(() => operation switch
        {
            "GetInfo" => (Task)connection.GetInfoAsync(Injected, ct),
            "List" => connection.ListAsync(Injected, ct),
            _ => connection.OpenReadAsync(Injected, 0, ct),
        });

        Assert.Empty(_server.Commands.Skip(mark));
    }
}
