using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

public sealed class DatasusClientTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListAsync_ReturnsFilesAndDirectoriesAndReusesTheListing()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/Sub/x.dbc", ClientFixture.Dbc("X"));
        var client = _fixture.Client();

        var first = await client.ListAsync(Dados, Ct);
        var second = await client.ListAsync(Dados + "/", Ct);

        Assert.Equal(["/SIASUS/200801_/Dados/PAMG2407a.dbc", "/SIASUS/200801_/Dados/Sub"], first.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Contains(first, entry => entry.Name == "Sub" && entry.IsDirectory);
        Assert.All(first, entry => Assert.Null(entry.LastModifiedUtc));
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListAsync_AfterTheListingExpires_FetchesAgain()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        await client.ListAsync(Dados, Ct);
        _fixture.Now += TimeSpan.FromMinutes(11);
        await client.ListAsync(Dados, Ct);

        Assert.Equal(2, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task GetInfoAsync_ReturnsExactSizeAndTime()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content, FakeServer.Time(7));

        var info = await _fixture.Client().GetInfoAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(content.Length, info.Size);
        Assert.Equal(FakeServer.Time(7), info.LastModifiedUtc);
        Assert.Equal("/SIASUS/200801_/Dados/RDMG2407.dbc", info.Path);
    }

    [Fact]
    public async Task GetInfoAsync_MissingFile_ThrowsFileNotFoundWithoutRetrying()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _fixture.Client().GetInfoAsync(Dados + "/NOPE2407.dbc", Ct));

        Assert.Equal(1, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindPartsAsync_UsesTheListingAndReturnsExactInfoPerPart()
    {
        foreach (var letter in "bac")
        {
            _fixture.Server.Add($"{Dados}/PAMG2407{letter}.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(letter - 'a'));
        }

        _fixture.Server.Add(Dados + "/PAMG2408a.dbc", ClientFixture.Dbc("A1"));

        var parts = await _fixture.Client().FindPartsAsync(Dados + "/PAMG2407.dbc", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc", "PAMG2407c.dbc"], parts.Select(part => part.Name));
        Assert.Equal(FakeServer.Time(1), parts[1].LastModifiedUtc);
    }

    [Fact]
    public async Task FindPartsAsync_GapOnTheServer_ThrowsFileNotFoundNamingThePart()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/PAMG2407c.dbc", ClientFixture.Dbc("A1"));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _fixture.Client().FindPartsAsync(Dados + "/PAMG2407a.dbc", Ct));

        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407b.dbc", exception.FileName);
    }

    [Fact]
    public async Task FindPartsAsync_UnsplitAndLetteredTogether_ThrowsIOException()
    {
        _fixture.Server.Add(Dados + "/PAMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));

        await Assert.ThrowsAsync<IOException>(() => _fixture.Client().FindPartsAsync(Dados + "/PAMG2407a.dbc", Ct));
    }

    [Fact]
    public async Task ReadHeaderAsync_ReadsOnlyTheHeaderBytes()
    {
        var content = ClientFixture.Dbc("A1", "B2", "C3");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);

        var header = await _fixture.Client().ReadHeaderAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(3, header.RecordCount);
        Assert.Equal(["CODE"], header.Fields.Select(field => field.Name));
        Assert.Equal(header.HeaderLength, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task DownloadAsync_WithoutCache_WritesATemporaryFile()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);

        var path = await _fixture.Client(cache: false).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.StartsWith(_fixture.TempDirectory, path, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public async Task DownloadAsync_WithCache_ReusesTheEntryAndRecordsTheUse()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content, FakeServer.Time(3));
        var client = _fixture.Client();

        var first = await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);
        _fixture.Now += TimeSpan.FromHours(1);
        var second = await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(first, second);
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.Equal(content, File.ReadAllBytes(second));
        var meta = File.ReadAllText(Path.Combine(Path.GetDirectoryName(second)!, "entry.meta"));
        Assert.Contains("path=/SIASUS/200801_/Dados/RDMG2407.dbc\n", meta, StringComparison.Ordinal);
        Assert.Contains("lastUsedUtc=2026-09-30T13:00:00.0000000Z\n", meta, StringComparison.Ordinal);
        Assert.Contains("crcVerified=true\n", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_ConnectionDropsMidTransfer_ResumesWhereItStopped()
    {
        var content = ClientFixture.Dbc([.. Enumerable.Range(0, 2000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);
        _fixture.Server.FailNextTransferAfter(5000);

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L], _fixture.Server.TransferOffsets);
        Assert.Equal(content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task DownloadAsync_FileRepublishedBeforeResuming_RestartsFromZeroAsANewVersion()
    {
        var original = ClientFixture.Dbc([.. Enumerable.Range(0, 2000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);
        var republished = ClientFixture.Dbc("NEW1", "NEW2");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", original, FakeServer.Time(1));
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(Dados + "/RDMG2407.dbc", republished, FakeServer.Time(2));
            }
        };

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(republished, File.ReadAllBytes(path));
        Assert.EndsWith("-20250808120200", Path.GetDirectoryName(path), StringComparison.Ordinal);
        Assert.Equal(0L, _fixture.Server.TransferOffsets.Last());
    }

    [Fact]
    public async Task DownloadAsync_TransientConnectFailures_AreRetried()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(2, () => new TransientTransportException("421 Too many connections"));

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DownloadAsync_RetriesExhausted_ThrowsRetriesExhaustedWithDetails()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(10, () => new TransientTransportException("421 Too many connections", "421"));

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => _fixture.Client(configure: o => o.MaxRetries = 2).DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("failed after 3 attempts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, exception.Attempts);
        Assert.Equal("421", exception.FtpReplyCode);
        Assert.Equal(Dados + "/RDMG2407.dbc", exception.RemotePath);
    }

    [Fact]
    public async Task DownloadAsync_ServerRefuses_ThrowsRequestRefusedWithoutRetrying()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(10, () => new DatasusRequestRefusedException("530 Not logged in", "530", null));

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Equal(1, exception.Attempts);
        Assert.Equal("530", exception.FtpReplyCode);
        Assert.Equal(Dados + "/RDMG2407.dbc", exception.RemotePath);
    }

    [Fact]
    public async Task OnRetry_ReceivesEveryRetryOfTheClient()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(2, () => new TransientTransportException("421 Too many connections", "421"));
        var reports = new List<DatasusRetryInfo>();

        await _fixture.Client(configure: o => o.OnRetry = reports.Add).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal([1, 2], reports.Select(report => report.Attempt));
        Assert.All(reports, report => Assert.Contains(Dados + "/RDMG2407.dbc", report.Operation, StringComparison.Ordinal));
        Assert.All(reports, report => Assert.Equal(Dados + "/RDMG2407.dbc", report.RemotePath));
    }

    [Fact]
    public async Task DownloadAsync_CorruptOnBothDownloads_ThrowsAndCachesNothing()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("failed its CRC-32 check after two downloads", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.Empty(Directory.EnumerateFiles(_fixture.CacheDirectory, "RDMG2407.dbc*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_CorruptOnceThenGood_SucceedsOnTheSecondDownload()
    {
        var good = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(Dados + "/RDMG2407.dbc", good);
            }
        };

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(2, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_VerificationDisabled_KeepsTheFileUnchecked()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));

        var path = await _fixture.Client(configure: o => o.VerifyOnDownload = false).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.True(File.Exists(path));
        Assert.Contains("crcVerified=false", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "entry.meta")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_NotEnoughDiskSpace_ThrowsBeforeTransferring()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.FreeSpace = 1024;

        var exception = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("Not enough disk space", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1024, exception.AvailableBytes);
        Assert.True(exception.RequiredBytes > exception.AvailableBytes);
        Assert.False(string.IsNullOrEmpty(exception.Directory));
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_Canceled_ThrowsAndLeavesNoCachedFile()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        using var cancellation = new CancellationTokenSource();
        _fixture.Server.BeforeTransfer = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(_fixture.CacheDirectory, "RDMG2407.dbc", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentRequestsForOneFile_TransferItOnce()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc([.. Enumerable.Range(0, 500).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]));
        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        var client = _fixture.Client();

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct))));

        Assert.Single(paths.Distinct());
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.InRange(_fixture.Server.MaxActiveConnections, 1, 2);
    }

    [Fact]
    public async Task DownloadAsync_ManyFilesInParallel_NeverExceedsMaxConnections()
    {
        for (var i = 0; i < 6; i++)
        {
            _fixture.Server.Add($"{Dados}/RD{i:D2}2407.dbc", ClientFixture.Dbc("A1", "B2"));
        }

        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(10);
        var client = _fixture.Client(configure: o => o.MaxConnections = 2);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => client.DownloadAsync($"{Dados}/RD{i:D2}2407.dbc", Ct))));

        Assert.Equal(6, _fixture.Server.Transfers);
        Assert.InRange(_fixture.Server.MaxActiveConnections, 1, 2);
    }

    [Fact]
    public async Task Methods_AfterDispose_ThrowObjectDisposedException()
    {
        var client = _fixture.Client();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListAsync(Dados, Ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Options_InvalidConnectionCount_ThrowsArgumentOutOfRange(int connections)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasusClient(new DatasusClientOptions { MaxConnections = connections }));
    }
}
