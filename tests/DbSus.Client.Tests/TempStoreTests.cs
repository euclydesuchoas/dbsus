using DbSus.Client.Caching;
using Xunit;

namespace DbSus.Client.Tests;

// The folders of downloads without a cache: one DbSus-<32 hex> per download, created only if
// nothing has its name, directly in the temporary directory; the clean-up removes only what it
// recognizes as its own (the name, the lock file, the file and its .partial), older than 24 hours,
// never enters a link, and never throws. What only a Linux run checks is marked as skipped here.
public sealed class TempStoreTests : IDisposable
{
    private const string Hex = "0123456789abcdef0123456789abcdef";
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "dbsus-temp-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = DateTime.UtcNow;

    public TempStoreTests()
    {
        Directory.CreateDirectory(Temp);
    }

    private string Temp => Path.Combine(_workspace, "temp");

    private string Outside => Path.Combine(_workspace, "outside");

    private TempStore Store => new(Temp, () => _now);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Create_MakesAFolderOfItsOwn_DirectlyInTheTemporaryDirectory()
    {
        var entry = Store.Create("RDRR2206.dbc");

        Assert.Equal(Temp, Path.GetDirectoryName(entry.Directory));
        Assert.Matches("^DbSus-[0-9a-f]{32}$", Path.GetFileName(entry.Directory));
        Assert.True(Directory.Exists(entry.Directory));
        Assert.Equal(Path.Combine(entry.Directory, "RDRR2206.dbc.partial"), entry.PartialPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateExclusive_NameAlreadyTaken_ThrowsAndLeavesItAsItIs(bool folder)
    {
        var path = Path.Combine(Temp, "DbSus-" + Hex);
        if (folder)
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "theirs.txt"), "synthetic");
        }
        else
        {
            File.WriteAllText(path, "synthetic");
        }

        var exception = Assert.Throws<IOException>(() => TempStore.CreateExclusive(path));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.True(folder ? File.Exists(Path.Combine(path, "theirs.txt")) : File.Exists(path));
    }

    [Theory]
    [InlineData]
    [InlineData("RDRR2206.dbc")]
    [InlineData("RDRR2206.dbc.partial")]
    [InlineData("RDRR2206.dbc", "RDRR2206.dbc.partial")]
    public void CleanOrphans_OldFolderOfOneDownload_IsRemoved(params string[] files)
    {
        var folder = OldDownload("DbSus-" + Hex, files);

        Store.CleanOrphans();

        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData("DbSus-0123456789abcdef0123456789abcde", "not 32 hex digits")]
    [InlineData("DbSus-0123456789ABCDEF0123456789ABCDEF", "upper case")]
    [InlineData("DbSus-0123456789abcdef0123456789abcdeg", "not hex")]
    [InlineData("Other-0123456789abcdef0123456789abcdef", "another prefix")]
    [InlineData("0123456789abcdef0123456789abcdef", "no prefix, outside the old folder")]
    public void CleanOrphans_FolderNamedOtherwise_IsKept(string name, string why)
    {
        var folder = OldDownload(name, "RDRR2206.dbc");

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")), why);
    }

    // An empty folder (its lock file deleted and the folder not, by an interrupted removal).
    [Theory]
    [InlineData("DbSus-0123456789abcdef0123456789abcdef")]
    [InlineData("DbSus/downloads/0123456789abcdef0123456789abcdef")]
    public void CleanOrphans_OldEmptyFolderWithoutItsLockFile_IsRemoved(string relative)
    {
        var folder = Path.Combine(Temp, relative);
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void CleanOrphans_RecentEmptyFolder_IsKept()
    {
        var folder = Path.Combine(Temp, "DbSus-" + Hex);
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, _now.AddHours(-23));

        Store.CleanOrphans();

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void CleanOrphans_OldEmptyFolderNamedOtherwise_IsKept()
    {
        var folder = Path.Combine(Temp, "DbSus-not-a-download");
        Directory.CreateDirectory(folder);
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void CleanOrphans_RecentFolder_IsKept()
    {
        var folder = OldDownload("DbSus-" + Hex, "RDRR2206.dbc");
        Directory.SetLastWriteTimeUtc(folder, _now.AddHours(-23));

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")));
    }

    [Fact]
    public void CleanOrphans_FolderWithoutItsLockFile_IsKept()
    {
        var folder = OldDownload("DbSus-" + Hex, "RDRR2206.dbc");
        File.Delete(Path.Combine(folder, "entry.lock"));
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")));
        Assert.False(File.Exists(Path.Combine(folder, "entry.lock")));
    }

    [Theory]
    [InlineData("RDRR2206.dbc", "RDRR2207.dbc")]
    [InlineData("RDRR2206.dbc", "RDRR2206.dbc.partial", "notes.txt")]
    public void CleanOrphans_FolderWithFilesOfAnotherKind_IsKept(params string[] files)
    {
        var folder = OldDownload("DbSus-" + Hex, files);

        Store.CleanOrphans();

        Assert.All(files, name => Assert.True(File.Exists(Path.Combine(folder, name)), name));
    }

    [Fact]
    public void CleanOrphans_FolderWithASubfolder_IsKept()
    {
        var folder = OldDownload("DbSus-" + Hex, "RDRR2206.dbc");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")));
    }

    // A failure on one folder (a file in use, a lock held elsewhere) leaves that folder and goes on.
    [Fact]
    public void CleanOrphans_FileInUseAndLockHeld_ThoseAreKept_TheOthersRemoved_NothingThrown()
    {
        var inUse = OldDownload("DbSus-" + new string('1', 32), "RDRR2206.dbc");
        var locked = OldDownload("DbSus-" + new string('2', 32), "RDRR2207.dbc");
        var free = OldDownload("DbSus-" + new string('3', 32), "RDRR2208.dbc");

        using (new FileStream(Path.Combine(inUse, "RDRR2206.dbc"), FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(Path.Combine(locked, "entry.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Store.CleanOrphans();
        }

        Assert.True(File.Exists(Path.Combine(inUse, "RDRR2206.dbc")));
        Assert.True(File.Exists(Path.Combine(locked, "RDRR2207.dbc")));
        Assert.False(Directory.Exists(free));
    }

    [Fact]
    public void CleanOrphans_NoTemporaryDirectory_DoesNothing()
    {
        new TempStore(Path.Combine(_workspace, "missing"), () => _now).CleanOrphans();

        Assert.False(Directory.Exists(Path.Combine(_workspace, "missing")));
    }

    // The folder of earlier versions, DbSus/downloads/<32 hex>, for one more version; then the
    // folders left empty.
    [Fact]
    public void CleanOrphans_FolderOfEarlierVersions_IsRemoved_WithItsEmptyParents()
    {
        var legacy = Path.Combine(Temp, "DbSus", "downloads");
        OldDownload(Path.Combine("DbSus", "downloads", Hex), "RDRR2206.dbc");

        Store.CleanOrphans();

        Assert.False(Directory.Exists(legacy));
        Assert.False(Directory.Exists(Path.Combine(Temp, "DbSus")));
    }

    [Fact]
    public void CleanOrphans_FolderOfEarlierVersionsNamedOtherwise_IsKept_AndSoAreItsParents()
    {
        var folder = OldDownload(Path.Combine("DbSus", "downloads", "not-a-download"), "RDRR2206.dbc");

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")));
    }

    [Fact]
    public void CleanOrphans_LinkNamedAsAFolderOfItsOwn_LeavesWhatIsBehindIt()
    {
        var behind = OutsideDownload();
        var link = Path.Combine(Temp, "DbSus-" + Hex);
        CacheLinkTests.MakeDirectoryLink(link, behind);

        Store.CleanOrphans();

        Assert.True(Directory.Exists(link));
        Assert.True(File.Exists(Path.Combine(behind, "RDRR2206.dbc")));
        Assert.True(File.Exists(Path.Combine(behind, "entry.lock")));
    }

    [Fact]
    public void CleanOrphans_LinkInsideAFolderOfItsOwn_KeepsTheFolder_AndWhatIsBehindTheLink()
    {
        var behind = OutsideDownload();
        var folder = OldDownload("DbSus-" + Hex, "RDRR2206.dbc");
        CacheLinkTests.MakeDirectoryLink(Path.Combine(folder, "link"), behind);
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(folder, "RDRR2206.dbc")));
        Assert.True(File.Exists(Path.Combine(behind, "RDRR2206.dbc")));
    }

    [Fact]
    public void CleanOrphans_FolderOfEarlierVersionsBehindALink_IsNotEntered()
    {
        var behind = Path.Combine(Outside, "DbSusElsewhere");
        var download = Path.Combine(behind, "downloads", Hex);
        Directory.CreateDirectory(download);
        File.WriteAllText(Path.Combine(download, "entry.lock"), string.Empty);
        File.WriteAllText(Path.Combine(download, "RDRR2206.dbc"), "synthetic");
        Directory.SetLastWriteTimeUtc(download, _now.AddDays(-2));
        CacheLinkTests.MakeDirectoryLink(Path.Combine(Temp, "DbSus"), behind);

        Store.CleanOrphans();

        Assert.True(File.Exists(Path.Combine(download, "RDRR2206.dbc")));
    }

#if NET
    // A symbolic link to a file: on Windows only where the system allows creating one (Developer
    // Mode or the privilege); always on Linux and macOS.
    [Fact]
    public void CleanOrphans_FolderWithALinkToAFile_IsKept_AndSoIsTheFileBehindIt()
    {
        Directory.CreateDirectory(Outside);
        var behind = Path.Combine(Outside, "theirs.dbc");
        File.WriteAllText(behind, "synthetic");
        var folder = OldDownload("DbSus-" + Hex);
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, "RDRR2206.dbc"), behind);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"This system does not let the test create a symbolic link ({exception.GetType().Name}).");
        }

        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));

        Store.CleanOrphans();

        Assert.True(File.Exists(behind));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void Create_OutsideWindows_TheFolderIsTheOwnersOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix permissions: checked on Linux and macOS only.");
            return;
        }

        var entry = Store.Create("RDRR2206.dbc");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(entry.Directory));
    }
#endif

    [Fact]
    public void TemporaryDownloadStream_Disposed_RemovesTheFileTheLockAndTheFolder()
    {
        var entry = Store.Create("RDRR2206.dbc");
        File.WriteAllText(entry.LockPath, string.Empty);
        File.WriteAllText(entry.DataPath, "synthetic");

        using (var stream = new TemporaryDownloadStream(entry.DataPath))
        {
            Assert.Equal((byte)'s', stream.ReadByte());
        }

        Assert.False(Directory.Exists(entry.Directory));
    }

    // Another file in the folder (none is expected): the folder and the lock stay, for a later clean-up.
    [Fact]
    public void TemporaryDownloadStream_Disposed_WithAnotherFileInTheFolder_KeepsTheFolderAndTheLock()
    {
        var entry = Store.Create("RDRR2206.dbc");
        File.WriteAllText(entry.LockPath, string.Empty);
        File.WriteAllText(entry.DataPath, "synthetic");
        File.WriteAllText(Path.Combine(entry.Directory, "other.txt"), "synthetic");

        new TemporaryDownloadStream(entry.DataPath).Dispose();

        Assert.False(File.Exists(entry.DataPath));
        Assert.True(File.Exists(entry.LockPath));
        Assert.True(File.Exists(Path.Combine(entry.Directory, "other.txt")));
    }

    // A folder like the ones Create makes, with its lock file and the given files, two days old.
    private string OldDownload(string relative, params string[] files)
    {
        var folder = Path.Combine(Temp, relative);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "entry.lock"), string.Empty);
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(folder, name), "synthetic");
        }

        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));
        return folder;
    }

    // A folder outside the temporary directory that looks like a download, two days old.
    private string OutsideDownload()
    {
        var folder = Path.Combine(Outside, "download");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "entry.lock"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "RDRR2206.dbc"), "synthetic");
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-2));
        return folder;
    }
}
