using DbSus.Client.Tests.Fakes;
using DbSus.Dbc;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

public sealed class CacheTests : IDisposable
{
    private const string Dados = "/SIHSUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DownloadAsync_EntryLockedByAnotherProcess_WaitsAndTimesOut()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(0));
        var client = _fixture.Client(configure: o => o.LockTimeout = TimeSpan.FromMilliseconds(300));
        var lockPath = EntryDirectory("RDRR2206.dbc", ClientFixture.Dbc("A1").Length, FakeServer.Time(0), "entry.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        // An exclusive open of the lock file is exactly what another DbSus process would hold.
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct));
        }

        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_EntryLockReleasedByAnotherProcess_ProceedsAndFindsItsWork()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", content, FakeServer.Time(0));
        var first = await _fixture.Client().DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        var lockPath = Path.Combine(Path.GetDirectoryName(first)!, "entry.lock");
        var holder = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var waiting = _fixture.Client().DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        holder.Dispose();

        Assert.Equal(first, await waiting);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task OpenReaderAsync_CachedCopyCorruptedOnDisk_InvalidatesItAndTheNextOpenDownloadsAgain()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1", "B2"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        var bytes = File.ReadAllBytes(local);
        bytes[bytes.Length - 3] ^= 0x01; // flip a bit of the compressed body, after download
        File.WriteAllBytes(local, bytes);
        var calls = new List<(int, string?)>();

        using (var reader = await client.OpenReaderAsync(Dados + "/RDRR2206.dbc", dbcOptions: new DbcReadOptions { ChecksumMismatch = (i, n) => calls.Add((i, n)) }, cancellationToken: Ct))
        {
            Assert.ThrowsAny<InvalidDataException>(() => Drain(reader));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(local)!, "entry.invalid")));
            Assert.True(File.Exists(local)); // open by the reader: marked, not deleted
        }

        using var again = await client.OpenReaderAsync(Dados + "/RDRR2206.dbc", cancellationToken: Ct);
        Assert.Equal(2, Drain(again));
        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(local)!, "entry.invalid")));
    }

    [Fact]
    public async Task DownloadAsync_InvalidEntryStillOpen_ThrowsInsteadOfReplacingIt()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(local)!, "entry.invalid"), "invalid\n");

        using (new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var exception = await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct));
            Assert.Contains("still open by a reader", exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DownloadAsync_CorruptMeta_DownloadsAgain()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(local)!, "entry.meta"), "garbage");

        await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);

        Assert.Equal(2, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task PruneCacheAsync_RemovesOnlyOldOrphansWhoseLockIsFree()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var directory = Path.GetDirectoryName(await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct))!;
        var oldOrphan = Write(directory, "old.partial", hoursAgo: 25);
        var recent = Write(directory, "recent.partial", hoursAgo: 1);

        await client.PruneCacheAsync(Ct);

        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(Path.Combine(directory, "RDRR2206.dbc")));
    }

    [Fact]
    public async Task PruneCacheAsync_OldOrphanWithLockHeld_IsKept()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var directory = Path.GetDirectoryName(await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct))!;
        var orphan = Write(directory, "old.partial", hoursAgo: 25);

        using (new FileStream(Path.Combine(directory, "entry.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await client.PruneCacheAsync(Ct);
        }

        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public async Task SizeLimit_RemovesLeastRecentlyUsedButNeverTheJustDownloadedOrInUse()
    {
        var content = ClientFixture.Dbc("A1");
        foreach (var name in new[] { "RDAA2206.dbc", "RDBB2206.dbc", "RDCC2206.dbc", "RDDD2206.dbc" })
        {
            _fixture.Server.Add($"{Dados}/{name}", content);
        }

        var client = _fixture.Client(configure: o => o.MaxCacheSizeBytes = 2 * content.Length);
        var a = await client.DownloadAsync(Dados + "/RDAA2206.dbc", Ct);
        _fixture.Now += TimeSpan.FromMinutes(1);
        var b = await client.DownloadAsync(Dados + "/RDBB2206.dbc", Ct);
        _fixture.Now += TimeSpan.FromMinutes(1);
        await client.DownloadAsync(Dados + "/RDAA2206.dbc", Ct); // A used again: B is now the least recently used
        _fixture.Now += TimeSpan.FromMinutes(1);
        var c = await client.DownloadAsync(Dados + "/RDCC2206.dbc", Ct);

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(c));

        // A is now the oldest, but it is open: D's download must evict nothing it cannot.
        _fixture.Now += TimeSpan.FromMinutes(1);
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var d = await client.DownloadAsync(Dados + "/RDDD2206.dbc", Ct);
            Assert.True(File.Exists(a));
            Assert.True(File.Exists(d));
        }
    }

    [Fact]
    public async Task DownloadAsync_NewVersionOnTheServer_ReplacesTheOldEntryWhenUnused()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(1));
        var client = _fixture.Client(configure: o => o.ListingCacheDuration = TimeSpan.Zero);
        var old = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("NEW1"), FakeServer.Time(2));

        var current = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);

        Assert.NotEqual(old, current);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public async Task ClearCacheAsync_RemovesEntriesNotInUse()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var local = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);

        await client.ClearCacheAsync(Ct);

        Assert.False(File.Exists(local));
    }

    [Fact]
    public async Task OpenReaderAsync_SplitTable_ReadsEveryPartWithTheirNames()
    {
        _fixture.Server.Add("/SIASUS/200801_/Dados/PAMG2407a.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add("/SIASUS/200801_/Dados/PAMG2407b.dbc", ClientFixture.Dbc("B1"));
        _fixture.Server.Add("/SIASUS/200801_/Dados/PAMG2407c.dbc", ClientFixture.CorruptDbc("C1"));
        var client = _fixture.Client(configure: o => o.VerifyOnDownload = false);

        using var reader = await client.OpenReaderAsync("/SIASUS/200801_/Dados/PAMG2407a.dbc", cancellationToken: Ct);
        var codes = new List<string>();
        var exception = Assert.ThrowsAny<InvalidDataException>(() =>
        {
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }
        });

        Assert.Equal(["A1", "A2", "B1", "C1"], codes);
        Assert.Equal(3, reader.PartCount);
        Assert.StartsWith("PAMG2407c.dbc: DBC checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReaderAsync_WithoutCache_DeletesTheTemporaryFilesOnDispose()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client(cache: false);

        using (var reader = await client.OpenReaderAsync(Dados + "/RDRR2206.dbc", cancellationToken: Ct))
        {
            Assert.NotEmpty(Directory.EnumerateFiles(_fixture.TempDirectory, "RDRR2206.dbc", SearchOption.AllDirectories));
            Assert.True(reader.Read());
        }

        // Disposed before the end: the temporary file still goes away.
        Assert.Empty(Directory.EnumerateFiles(_fixture.TempDirectory, "RDRR2206.dbc", SearchOption.AllDirectories));
    }

    private static int Drain(DbfReader reader)
    {
        var count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }

    private static string Write(string directory, string name, int hoursAgo)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc).AddHours(-hoursAgo));
        return path;
    }

    private string EntryDirectory(string name, long size, DateTime modified, string file) =>
        Path.Combine(_fixture.CacheDirectory, "SIHSUS", "200801_", "Dados", name, $"{size}-{modified:yyyyMMddHHmmss}", file);
}
