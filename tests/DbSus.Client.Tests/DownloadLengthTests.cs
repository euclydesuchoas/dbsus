using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A download reads at most the size the server announced for the file: what a server sends
// beyond it is never written, and the transfer counts as a transient failure.
public sealed class DownloadLengthTests : IDisposable
{
    private const string File = "/SIHSUS/200801_/Dados/RDMG2407.dbc";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    private static byte[] Content(int records) =>
        ClientFixture.Dbc([.. Enumerable.Range(0, records).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);

    [Fact]
    public async Task DownloadAsync_ServerSendsExactlyTheSize_TakesOneTransfer()
    {
        var content = Content(2000);
        _fixture.Server.Add(File, content);

        var path = await _fixture.Client().DownloadAsync(File, Ct);

        Assert.Equal(content, System.IO.File.ReadAllBytes(path));
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.Equal(content.Length, _fixture.Server.BytesServed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_ServerSendsMoreOnce_RetriesAndKeepsOnlyTheFile(bool cache)
    {
        var content = Content(2000);
        _fixture.Server.Add(File, content);
        _fixture.Server.SendExtraBytes(transfers: 1, bytes: 100_000);

        var path = await _fixture.Client(cache).DownloadAsync(File, Ct);

        Assert.Equal(content, System.IO.File.ReadAllBytes(path));
        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.Equal([0L, 0L], _fixture.Server.TransferOffsets);
        Assert.False(System.IO.File.Exists(path + ".partial"));

        // The first transfer was read up to one byte past the size, not to the end of what was sent.
        Assert.Equal((2L * content.Length) + 1, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task DownloadAsync_ServerAlwaysSendsMore_FailsAfterTheRetriesAndKeepsNothing()
    {
        _fixture.Server.Add(File, Content(2000));
        _fixture.Server.SendExtraBytes(transfers: 10, bytes: 1);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(
            () => _fixture.Client(configure: options => options.MaxRetries = 2).DownloadAsync(File, Ct));

        Assert.Equal(3, exception.Attempts);
        Assert.Contains("sent more than", exception.InnerException!.Message, StringComparison.Ordinal);
        Assert.Equal(File, exception.RemotePath);
        Assert.Empty(Directory.EnumerateFiles(_fixture.CacheDirectory, "RDMG2407.dbc*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_ResumedTransferSendsMore_StartsAgainFromZero()
    {
        var content = Content(2000);
        _fixture.Server.Add(File, content);
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.SendExtraBytes(transfers: 1, bytes: 10);
            }
        };

        var path = await _fixture.Client().DownloadAsync(File, Ct);

        Assert.Equal(content, System.IO.File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L, 0L], _fixture.Server.TransferOffsets);
    }

    [Fact]
    public async Task DownloadAsync_FileGrowsDuringTheTransfer_DownloadsTheNewVersion()
    {
        var original = Content(10);
        var grown = Content(2000);
        _fixture.Server.Add(File, original, FakeServer.Time(1));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 1)
            {
                _fixture.Server.Add(File, grown, FakeServer.Time(2));
            }
        };

        var path = await _fixture.Client().DownloadAsync(File, Ct);

        Assert.Equal(grown, System.IO.File.ReadAllBytes(path));
        Assert.EndsWith("-20250808120200", Path.GetDirectoryName(path), StringComparison.Ordinal);
        Assert.Equal(2, _fixture.Server.Transfers);
    }
}
