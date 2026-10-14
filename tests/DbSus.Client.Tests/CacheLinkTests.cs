using System.Diagnostics;
using System.Runtime.InteropServices;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A junction or a symbolic link inside the cache, or inside the folder of the temporary downloads,
// is never entered by enumeration or clean-up: what is behind it stays where it is.
public sealed class CacheLinkTests : IDisposable
{
    private const string Dados = "/SIHSUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Outside => Path.Combine(_fixture.Workspace, "outside");

    // A folder that looks like a cache entry, with its data file, outside the cache.
    private string OutsideEntryFile(params string[] extra)
    {
        var version = Path.Combine(Outside, "X.dbc", "123-20250808120000");
        Directory.CreateDirectory(version);
        var data = Path.Combine(version, "X.dbc");
        File.WriteAllText(data, "synthetic");
        foreach (var name in extra)
        {
            File.WriteAllText(Path.Combine(version, name), "synthetic");
        }

        return data;
    }

    [Fact]
    public async Task ClearCacheAsync_LinkInsideTheCache_LeavesWhatIsBehindItAndTheLink()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var cached = await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        var outside = OutsideEntryFile("entry.meta");
        var link = Path.Combine(_fixture.CacheDirectory, "link");
        MakeDirectoryLink(link, Outside);

        await client.ClearCacheAsync(Ct);

        Assert.False(File.Exists(cached));
        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public async Task PruneCacheAsync_LinkToAnInvalidLookingEntry_LeavesIt()
    {
        _fixture.Server.Add(Dados + "/RDRR2206.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client(configure: options => options.MaxCacheSizeBytes = 1);
        await client.DownloadAsync(Dados + "/RDRR2206.dbc", Ct);
        var outside = OutsideEntryFile("entry.invalid");
        MakeDirectoryLink(Path.Combine(_fixture.CacheDirectory, "link"), Outside);

        await client.PruneCacheAsync(Ct);

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task PruneCacheAsync_OldLinkAmongTheTemporaryDownloads_LeavesWhatIsBehindIt()
    {
        var outside = OutsideEntryFile();
        Directory.CreateDirectory(_fixture.TempDirectory);
        var link = Path.Combine(_fixture.TempDirectory, "0123456789abcdef0123456789abcdef");
        MakeDirectoryLink(link, Path.GetDirectoryName(outside)!);
        _fixture.Now = DateTime.UtcNow.AddDays(2);

        await _fixture.Client(cache: false).PruneCacheAsync(Ct);

        Assert.True(File.Exists(outside));
        Assert.True(Directory.Exists(link));
    }

    // A junction on Windows, which needs no privilege; a symbolic link elsewhere.
    private static void MakeDirectoryLink(string link, string target)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
#if NET
            Directory.CreateSymbolicLink(link, target);
#else
            throw new PlatformNotSupportedException("The .NET Framework tests run on Windows only.");
#endif
        }

        Assert.True((new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0);
    }
}
