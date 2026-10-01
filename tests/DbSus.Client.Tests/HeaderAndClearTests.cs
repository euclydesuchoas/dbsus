using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

public sealed class HeaderAndClearTests : IDisposable
{
    private const string Dados = "/SIHSUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadHeaderAsync_ParallelCalls_RespectMaxConnections()
    {
        for (var i = 0; i < 6; i++)
        {
            _fixture.Server.Add($"{Dados}/RD{i:D2}2206.dbc", ClientFixture.Dbc("A1"));
        }

        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(20);
        var client = _fixture.Client(configure: o => o.MaxConnections = 1);

        var headers = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => client.ReadHeaderAsync($"{Dados}/RD{i:D2}2206.dbc", Ct))));

        Assert.All(headers, header => Assert.Equal(1, header.RecordCount));
        Assert.Equal(1, _fixture.Server.MaxActiveConnections);
    }

    [Fact]
    public async Task ReadHeaderAsync_ConnectionDrops_ReconnectsAfterBackoff()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1", "B2"));
        _fixture.Server.FailNextTransferAfter(10);
        var client = _fixture.Client();

        var header = await client.ReadHeaderAsync(Dados + "/RDRR2206.dbc", Ct);

        Assert.Equal(2, header.RecordCount);
        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.Equal(2, _fixture.Server.ConnectionsOpened); // the aborted connection is discarded, not reused
        var delay = Assert.Single(_fixture.Delays);
        Assert.InRange(delay, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ReadHeaderAsync_EveryCallDiscardsItsConnection()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        await client.ReadHeaderAsync(Dados + "/RDRR2206.dbc", Ct);
        await client.ReadHeaderAsync(Dados + "/RDRR2206.dbc", Ct);

        Assert.Equal(2, _fixture.Server.ConnectionsOpened);
        Assert.Equal(0, _fixture.Server.ActiveConnections);
    }

    [Fact]
    public async Task ClearCacheAsync_RemovesEntriesLockFilesAndEmptyFolders()
    {
        _fixture.Server.Add(Dados + "/RDAA2206.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add("/SIASUS/200801_/Dados/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        await client.DownloadAsync(Dados + "/RDAA2206.dbc", Ct);
        await client.DownloadAsync("/SIASUS/200801_/Dados/PAMG2407a.dbc", Ct);

        await client.ClearCacheAsync(Ct);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_fixture.CacheDirectory));
    }

    [Fact]
    public async Task ClearCacheAsync_EntryInUse_KeepsItsFilesAndFolders()
    {
        _fixture.Server.Add(Dados + "/RDAA2206.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/RDBB2206.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();
        var inUse = await client.DownloadAsync(Dados + "/RDAA2206.dbc", Ct);
        var unused = await client.DownloadAsync(Dados + "/RDBB2206.dbc", Ct);

        using (new FileStream(inUse, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await client.ClearCacheAsync(Ct);
        }

        Assert.True(File.Exists(inUse));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(inUse)!, "entry.lock")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(unused)));
    }

    [Fact]
    public async Task PruneCacheAsync_KeepsLockFiles()
    {
        _fixture.Server.Add(Dados + "/RDAA2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDAA2206.dbc", Ct);

        await client.PruneCacheAsync(Ct);

        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(local)!, "entry.lock")));
    }
}
