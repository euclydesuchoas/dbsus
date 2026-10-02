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
        await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => FtpConnection.Translate(async () =>
        {
            await FtpConnection.Expect(Reply("530", "Not logged in"), "/x.dbc");
            return 1;
        }));
    }

    private static Task<FtpReply> Reply(string code, string message) => Task.FromResult(new FtpReply { Code = code, Message = message });
}
