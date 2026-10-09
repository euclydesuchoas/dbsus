using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

/// <summary>
/// What happens when a cache entry cannot be completed: data without metadata, and a temporary
/// file left by a process that stopped.
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

    [Fact]
    public async Task DownloadAsync_OrphanMetaTemporary_IsOverwrittenByTheNextWrite()
    {
        _fixture.Server.Add(File1, ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(File1, Ct);
        var directory = Path.GetDirectoryName(local)!;
        var orphan = Path.Combine(directory, "entry.meta.tmp");
        File.WriteAllText(orphan, "left by a process that stopped while writing the metadata");
        _fixture.Now = _fixture.Now.AddHours(1);

        await client.DownloadAsync(File1, Ct); // a cache hit records the use: the metadata is written again

        Assert.False(File.Exists(orphan));
        Assert.Equal(["RDRR2206.dbc", "entry.lock", "entry.meta"], Directory.GetFiles(directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Contains("lastUsedUtc=2026-09-30T13:00:00.0000000Z", File.ReadAllText(Path.Combine(directory, "entry.meta")), StringComparison.Ordinal);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    // As left by a metadata write that failed: the data file is there, possibly unverified, and
    // there is no entry.meta.
    private static void UnverifiedCopyWithoutMeta(string local, int length)
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(local)!, "entry.meta"));
        File.WriteAllBytes(local, Enumerable.Repeat((byte)0xAA, length).ToArray());
    }
}
