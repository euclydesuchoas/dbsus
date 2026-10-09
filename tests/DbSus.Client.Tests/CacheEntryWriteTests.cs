using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

/// <summary>
/// What happens when a cache entry cannot be completed: data without metadata, a temporary file
/// left by a process that stopped, and paths too long for .NET Framework without long path support.
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

    // The entry folder can be created, but not the download's .partial inside it: the client must
    // fail before the transfer, with a message that points at the cache path. Each case has one
    // test per branch; the branch that does not apply to this runtime and machine is skipped, with
    // the reason, and the one that runs says so in its output.
    [Fact]
    public async Task DownloadAsync_PartialPathTooLong_WithoutLongPaths_FailsBeforeAnyTransfer()
    {
        await AssertFailsBeforeAnyTransfer(entryDirectoryLength: 243);
    }

    [Fact]
    public async Task DownloadAsync_PartialPathTooLong_WithLongPaths_Downloads()
    {
        await AssertDownloads(entryDirectoryLength: 243);
    }

    // The entry folder itself cannot be created.
    [Fact]
    public async Task DownloadAsync_EntryFolderTooLong_WithoutLongPaths_FailsBeforeAnyTransfer()
    {
        await AssertFailsBeforeAnyTransfer(entryDirectoryLength: 252);
    }

    [Fact]
    public async Task DownloadAsync_EntryFolderTooLong_WithLongPaths_Downloads()
    {
        await AssertDownloads(entryDirectoryLength: 252);
    }

    private async Task AssertFailsBeforeAnyTransfer(int entryDirectoryLength)
    {
        if (LongPathsWork())
        {
            Assert.Skip("Long paths work on this runtime and machine, so a deep cache root does not fail here; the WithLongPaths test covers this case.");
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Branch: long paths do not work here; an entry folder of {entryDirectoryLength} characters must fail before any transfer.");
        var (client, _) = DeepCacheRoot(entryDirectoryLength);

        var exception = await Assert.ThrowsAsync<IOException>(() => client.DownloadAsync(File1, Ct));

        Assert.True(exception.InnerException is DirectoryNotFoundException or PathTooLongException, exception.InnerException?.GetType().FullName);
        Assert.StartsWith("Could not write the cache entry of " + File1 + ": ", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith(
            " On .NET Framework without long path support, the entry path may be too long: use a shorter CacheDirectory.",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    private async Task AssertDownloads(int entryDirectoryLength)
    {
        if (!LongPathsWork())
        {
            Assert.Skip("Long paths do not work on this runtime and machine (.NET Framework without long path support); the WithoutLongPaths test covers this case.");
        }

        // .NET 8 or later, or .NET Framework with long paths enabled: nothing changes.
        TestContext.Current.TestOutputHelper?.WriteLine($"Branch: long paths work here; an entry folder of {entryDirectoryLength} characters must download normally.");
        var (client, content) = DeepCacheRoot(entryDirectoryLength);

        var local = await client.DownloadAsync(File1, Ct);

        Assert.Equal(content, File.ReadAllBytes(local));
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    // A client whose cache root makes the entry folder of File1 exactly that long.
    private (DatasusClient Client, byte[] Content) DeepCacheRoot(int entryDirectoryLength)
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(File1, content);
        var suffix = Path.Combine("SIHSUS", "200801_", "Dados", "RDRR2206.dbc", content.Length + "-20250808120000");
        var padding = entryDirectoryLength - _fixture.Workspace.Length - 2 - suffix.Length;
        var root = Path.Combine(_fixture.Workspace, new string('c', padding));
        Assert.Equal(entryDirectoryLength, Path.Combine(root, suffix).Length);
        return (_fixture.Client(configure: options => options.CacheDirectory = root), content);
    }

    // Whether this runtime and machine can create a file whose path exceeds 260 characters.
    private bool LongPathsWork()
    {
        var directory = Path.Combine(_fixture.Workspace, "probe", new string('p', 120), new string('q', 120));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "probe.txt"), string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or PathTooLongException)
        {
            return false;
        }
    }

    // As left by a metadata write that failed: the data file is there, possibly unverified, and
    // there is no entry.meta.
    private static void UnverifiedCopyWithoutMeta(string local, int length)
    {
        File.Delete(Path.Combine(Path.GetDirectoryName(local)!, "entry.meta"));
        File.WriteAllBytes(local, Enumerable.Repeat((byte)0xAA, length).ToArray());
    }
}
