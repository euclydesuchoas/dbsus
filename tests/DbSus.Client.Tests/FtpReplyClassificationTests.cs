using DbSus.Client.Transport;
using FluentFTP;
using Xunit;

namespace DbSus.Client.Tests;

public class FtpReplyClassificationTests
{
    [Fact]
    public async Task Expect_550_IsFileNotFound()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => FtpConnection.Expect(Reply("550", "Not found"), "/x.dbc"));
    }

    [Fact]
    public async Task Expect_4xx_IsTransientWithTheReplyCode()
    {
        var exception = await Assert.ThrowsAsync<TransientTransportException>(() => FtpConnection.Expect(Reply("421", "Too many connections"), "/x.dbc"));

        Assert.Equal("421", exception.ReplyCode);
    }

    [Fact]
    public async Task Expect_OtherFailure_IsARefusalWithTheReplyCode()
    {
        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => FtpConnection.Expect(Reply("530", "Not logged in"), "/x.dbc"));

        Assert.Equal("530", exception.FtpReplyCode);
        Assert.IsAssignableFrom<IOException>(exception);
    }

    [Fact]
    public async Task Translate_PermanentReplyInsideAnOperation_StaysARefusal()
    {
        await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => FtpConnection.Translate(Listing, null, async () =>
        {
            await FtpConnection.Expect(Reply("530", "Not logged in"), "/x.dbc");
            return 1;
        }));
    }

    [Fact]
    public async Task Translate_ConnectTimeoutWhileListing_NamesTheStepAndAPossibleCause()
    {
        var exception = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate<int>(Listing, () => 1, () => throw new TimeoutException("Timed out trying to connect to IP #1")));

        Assert.Equal(
            "FTP transport failure while listing /dissemin/publicos/X on ftp.example:21: Timed out trying to connect to IP #1. "
            + "The control connection was not reopened during this step; a possible cause is a passive data connection being blocked "
            + "(for example by a firewall or NAT that lets port 21 through but not the server's passive ports).",
            exception.Message);
    }

    [Fact]
    public async Task Translate_ControlConnectionReopenedDuringTheStep_SaysEitherConnectionMayHaveFailed()
    {
        var connects = 1;

        var exception = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate<int>(Listing, () => connects, () =>
            {
                connects++;
                throw new TimeoutException("Timed out trying to connect to IP #1");
            }));

        Assert.Contains("The control connection was reopened during this step, so the connection attempt that failed may be the control connection or the passive data connection", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Translate_ReadTimeout_HasNoConnectionHint()
    {
        var exception = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate<int>(Listing, () => 1, () => throw new TimeoutException("Timed out trying to read data from the socket stream!")));

        Assert.Equal("FTP transport failure while listing /dissemin/publicos/X on ftp.example:21: Timed out trying to read data from the socket stream!", exception.Message);
    }

    [Fact]
    public async Task Translate_ConnectFailureWhileConnecting_HasNoPassiveHint()
    {
        var exception = await Assert.ThrowsAsync<TransientTransportException>(() =>
            FtpConnection.Translate<int>(FtpStep.Connecting("ftp.example", 21), null, () => throw new TimeoutException("Timed out trying to connect to IP #1")));

        Assert.Equal("FTP transport failure while connecting to ftp.example:21: Timed out trying to connect to IP #1", exception.Message);
    }

    private static FtpStep Listing => FtpStep.Listing("/dissemin/publicos/X", "ftp.example", 21);

    private static Task<FtpReply> Reply(string code, string message) => Task.FromResult(new FtpReply { Code = code, Message = message });
}
