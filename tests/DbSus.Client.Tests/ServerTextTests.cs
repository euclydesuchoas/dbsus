using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// Text from the server goes into exception messages only with its control, format and separator
// characters escaped, and cut at ServerText.MaxLength. Exceptions that FluentFTP raises, kept as
// inner exceptions, are not changed: the last test shows that they still carry the raw text.
public sealed class ServerTextTests : IDisposable
{
    private const string Escape = "\u001b";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("a\u001b[31mb", "a\\x1B[31mb")]
    [InlineData("tab\there\r\nnext", "tab\\x09here\\x0D\\x0Anext")]
    [InlineData("del\u007f c1\u0085", "del\\x7F c1\\x85")]
    [InlineData("rtl\u202eabc\u202c", "rtl\\u202Eabc\\u202C")]
    [InlineData("marks\u200e\u200f iso\u2066\u2069", "marks\\u200E\\u200F iso\\u2066\\u2069")]
    [InlineData("line\u2028para\u2029", "line\\u2028para\\u2029")]
    [InlineData("acentua\u00e7\u00e3o", "acentua\u00e7\u00e3o")]
    [InlineData("", "")]
    public void Clean_EscapesUnsafeCharacters_AndKeepsTheRest(string text, string expected) =>
        Assert.Equal(expected, ServerText.Clean(text));

    // A lone half of a surrogate pair cannot go through an attribute, so it is built here.
    [Fact]
    public void Clean_EscapesALoneSurrogate_AndKeepsAPair()
    {
        var text = "pair \ud83d\ude00 lone " + (char)0xD83D;

        Assert.Equal("pair \ud83d\ude00 lone \\uD83D", ServerText.Clean(text));
    }

    [Fact]
    public void Clean_Null_IsEmpty() => Assert.Equal(string.Empty, ServerText.Clean(null));

    [Fact]
    public void Clean_LongText_IsCutAndSaysHowMuchWasLeftOut()
    {
        var text = new string('a', 2000);

        var clean = ServerText.Clean(text);

        Assert.StartsWith(new string('a', ServerText.MaxLength), clean, StringComparison.Ordinal);
        Assert.EndsWith($"... ({2000 - ServerText.MaxLength} more characters)", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_DoesNotCutAnEscapeInTwo()
    {
        var text = new string('a', ServerText.MaxLength - 2) + Escape + "tail";

        var clean = ServerText.Clean(text);

        Assert.DoesNotContain("\\x1", clean, StringComparison.Ordinal);
        Assert.EndsWith("... (5 more characters)", clean, StringComparison.Ordinal);
    }

    // A reply that the client reads itself (SIZE): the message and the text given to OnRetry.
    [Fact(Timeout = 30_000)]
    public async Task SizeReplyWithAnEscape_IsEscapedInTheMessageAndInOnRetry()
    {
        _server.SizeReply = "213 1" + Escape + "[31mRED";
        var retries = new List<DatasusRetryInfo>();
        using var client = Client(retries: 1, retries.Add);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.GetInfoAsync("/X/A.dbf", TestContext.Current.CancellationToken));

        Assert.Contains("with '1\\x1B[31mRED'", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Escape, exception.Message, StringComparison.Ordinal);
        var retry = Assert.Single(retries);
        Assert.Contains("\\x1B", retry.Exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Escape, retry.Exception.Message, StringComparison.Ordinal);
    }

    // A reply that FluentFTP turns into its own exception (a refused login, FtpAuthenticationException):
    // the client's message is escaped; the FluentFTP exception, kept as the inner exception, still
    // has the raw text.
    [Fact(Timeout = 30_000)]
    public async Task LoginRefusedWithAnEscape_MessageIsEscaped_InnerExceptionOfFluentFtpIsNot()
    {
        _server.PassReply = "530 Login" + Escape + "[31m incorrect.";
        using var client = Client(retries: 0);

        var exception = await Assert.ThrowsAnyAsync<DatasusTransferException>(() => client.ListAsync("/X", TestContext.Current.CancellationToken));

        var inner = exception.InnerException;
        while (inner is not null and not FluentFTP.Exceptions.FtpCommandException)
        {
            inner = inner.InnerException;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{exception.GetType().Name}: {exception.Message} | FluentFTP exception: {inner?.GetType().FullName ?? "none"}: {ServerText.Clean(inner?.Message)}");
        Assert.Contains("Login\\x1B[31m incorrect.", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Escape, exception.Message, StringComparison.Ordinal);
        Assert.IsType<FluentFTP.Exceptions.FtpAuthenticationException>(inner);
        Assert.Contains(Escape, inner!.Message, StringComparison.Ordinal);
    }

    private DatasusClient Client(int retries, Action<DatasusRetryInfo>? onRetry = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = retries,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReadTimeout = TimeSpan.FromSeconds(2),
        OnRetry = onRetry,
    });
}
