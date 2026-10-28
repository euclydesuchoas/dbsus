using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// A SIZE or MDTM reply that is not a size or a time is a transport failure, classified like the
// others, and never a FormatException, OverflowException or ArgumentOutOfRangeException.
public sealed class InfoReplyFormatTests : IDisposable
{
    private const string Path = "/dissemin/publicos/X/RDRR2206.dbc";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("1 213")]
    [InlineData("99999999999999999999")]
    public void ParseSize_NotASize_IsTransient(string reply)
    {
        var exception = Assert.Throws<TransientTransportException>(() => FtpConnection.ParseSize(reply, Path));

        Assert.Contains($"The server answered SIZE for {Path} with '{reply.Trim()}', which is not a size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseSize_Digits_IsTheSize() => Assert.Equal(1213L, FtpConnection.ParseSize(" 1213 ", Path));

    [Theory]
    [InlineData("2025080812")]
    [InlineData("20251399120000")]
    [InlineData("20250230120000")]
    [InlineData("2025080812000a")]
    [InlineData("abcdefghijklmn")]
    [InlineData("")]
    public void ParseModified_NotATime_IsTransient(string reply)
    {
        var exception = Assert.Throws<TransientTransportException>(() => FtpConnection.ParseModified(reply, Path));

        Assert.Contains("which is not a time", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20250808120000")]
    [InlineData("20250808120000.123")]
    public void ParseModified_Time_IsUtc(string reply)
    {
        var modified = FtpConnection.ParseModified(reply, Path);

        Assert.Equal(new DateTime(2025, 8, 8, 12, 0, 0, DateTimeKind.Utc), modified);
        Assert.Equal(DateTimeKind.Utc, modified.Kind);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("213 abc", null)]
    [InlineData("213 99999999999999999999", null)]
    [InlineData(null, "213 2025")]
    [InlineData(null, "213 20251399120000")]
    public async Task GetInfoAsync_MalformedReply_IsRetriedAndReportedAsATransferFailure(string? sizeReply, string? mdtmReply)
    {
        _server.SizeReply = sizeReply;
        _server.MdtmReply = mdtmReply;

        // Timeouts that a slow runner does not reach: the malformed reply comes at once, and a
        // timeout would be one more failure to count.
        using var client = new DatasusClient(new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            MaxRetries = 1,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ReadTimeout = TimeSpan.FromSeconds(10),
        });

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.GetInfoAsync("/X/RDRR2206.dbc", TestContext.Current.CancellationToken));

        Assert.Equal(2, exception.Attempts);
        Assert.Equal("/X/RDRR2206.dbc", exception.RemotePath);
        Assert.IsType<TransientTransportException>(exception.InnerException);
    }
}
