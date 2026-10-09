using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

/// <summary>
/// What happens when a cache entry cannot be completed: data without metadata.
/// </summary>
public sealed class CacheEntryWriteTests : IDisposable
{
    private const string Dados = "/SIHSUS/200801_/Dados";
    private const string File1 = Dados + "/RDRR2206.dbc";
    private readonly ClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadAsync_DataCachedWithoutMeta_DownloadsAgainInsteadOfReturningIt()
    {
        var content = ClientFixture.Dbc("A1", "B2");
        _fixture.Server.Add(File1, content);
        var client = _fixture.Client();
        var local = await client.DownloadAsync(File1, Ct);
        UnverifiedCopyWithoutMeta(local, content.Length);

        var again = await client.DownloadAsync(File1, Ct);

        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.Equal(content, File.ReadAllBytes(again));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(again)!, "entry.meta")));
    }

    [Fact]
    public async Task OpenReaderAsync_DataCachedWithoutMeta_ReadsADownloadedCopy()
    {
        var content = ClientFixture.Dbc("A1", "B2");
        _fixture.Server.Add(File1, content);
        var client = _fixture.Client();
        var local = await client.DownloadAsync(File1, Ct);
        UnverifiedCopyWithoutMeta(local, content.Length);

        using var reader = await client.OpenReaderAsync(File1, cancellationToken: Ct);
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        Assert.Equal(["A1", "B2"], codes);
        Assert.Equal(2, _fixture.Server.Transfers);
    }

    // As left by a metadata write that failed: the data file is there, possibly unverified, and
    // there is no entry.meta.
    private static void UnverifiedCopyWithoutMeta(string local, int length)
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(local)!, "entry.meta"));
        File.WriteAllBytes(local, Enumerable.Repeat((byte)0xAA, length).ToArray());
    }
}
