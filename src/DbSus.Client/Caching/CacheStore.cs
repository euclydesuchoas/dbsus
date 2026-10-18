using System.Globalization;
using System.Text.RegularExpressions;
using DbSus.Client.Transport;

namespace DbSus.Client.Caching;

/// <summary>
/// One cached version of a remote file: <c>&lt;cache&gt;/&lt;remote path&gt;/&lt;size&gt;-&lt;MDTM&gt;/</c>.
/// Each version has its own folder, so a newer version is downloaded next to an older one that a
/// reader may still have open, never over it.
/// </summary>
internal sealed class CacheEntry(string directory, string fileName)
{
    public string Directory { get; } = directory;

    public string DataPath { get; } = Path.Combine(directory, fileName);

    public string PartialPath { get; } = Path.Combine(directory, fileName + ".partial");

    public string MetaPath { get; } = Path.Combine(directory, "entry.meta");

    public string LockPath { get; } = Path.Combine(directory, "entry.lock");

    public string InvalidPath { get; } = Path.Combine(directory, "entry.invalid");

    public bool IsMarkedInvalid => File.Exists(InvalidPath);

    public EntryMeta? ReadMeta() => EntryMeta.TryRead(MetaPath);

    public bool IsValidFor(RemotePath path, RemoteFileInfo info)
    {
        var meta = ReadMeta();
        return meta is not null
            && !IsMarkedInvalid
            && string.Equals(meta.Path, path.Relative, StringComparison.Ordinal)
            && meta.Size == info.Size
            && meta.ModifiedUtc == info.LastModifiedUtc
            && File.Exists(DataPath)
            && new FileInfo(DataPath).Length == info.Size;
    }

    /// <summary>Marks the entry as corrupt without touching the data, which may be open.</summary>
    public void MarkInvalid()
    {
        try
        {
            File.WriteAllText(InvalidPath, "invalid\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records a use for the least-recently-used policy (own mark, not file-system access times).</summary>
    public void Touch(DateTime utcNow)
    {
        try
        {
            ReadMeta()?.WithLastUsed(utcNow).WriteAtomically(MetaPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes the cached data and its metadata, unless a reader has the data open.</summary>
    public bool TryRemove()
    {
        if (!FileLocks.TryDeleteIfUnused(DataPath))
        {
            return false;
        }

        FileLocks.TryDelete(MetaPath);
        FileLocks.TryDelete(InvalidPath);
        FileLocks.TryDelete(PartialPath);
        return true;
    }
}

/// <summary>The persistent cache: entry layout, orphan clean-up and the size limit.</summary>
internal sealed class CacheStore(string root, long? maxBytes, Func<DateTime> utcNow)
{
    public static readonly TimeSpan OrphanAge = TimeSpan.FromHours(24);

    private static readonly Regex VersionName = new(@"^\d+-\d{14}$", RegexOptions.CultureInvariant);

    public string Root { get; } = Path.GetFullPath(root);

    public CacheEntry EntryFor(RemotePath path, RemoteFileInfo info)
    {
        var version = info.Size.ToString(CultureInfo.InvariantCulture) + "-"
            + info.LastModifiedUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine([Root, .. path.Segments, version]);
        return new CacheEntry(directory, path.Name);
    }

    public List<CacheEntry> Entries()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        return
        [
            .. DirectoriesBelow(Root)
                .Where(directory => VersionName.IsMatch(Path.GetFileName(directory)))
                .Select(directory => new CacheEntry(directory, Path.GetFileName(Path.GetDirectoryName(directory)!))),
        ];
    }

    /// <summary>
    /// Tells whether a folder is a junction or a symbolic link. Enumeration and clean-up never enter
    /// one: a link inside the cache could otherwise make them delete files outside it.
    /// </summary>
    internal static bool IsLink(DirectoryInfo directory) => (directory.Attributes & FileAttributes.ReparsePoint) != 0;

    // Every folder below a folder, links excluded and not entered.
    private static IEnumerable<string> DirectoriesBelow(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().EnumerateDirectories())
            {
                if (IsLink(child))
                {
                    continue;
                }

                yield return child.FullName;
                pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Deletes orphans (partial downloads and temporary files older than 24 hours whose entry lock
    /// is free), entries marked invalid that nobody uses, and, above the size limit, the least
    /// recently used entries that are not in use. <paramref name="keep"/> (the entry just
    /// downloaded) is never touched.
    /// </summary>
    public void Prune(CacheEntry? keep)
    {
        var now = utcNow();
        var entries = Entries().Where(entry => !IsSame(entry, keep)).ToList();
        foreach (var entry in entries)
        {
            using var held = FileLocks.TryAcquire(entry.LockPath);
            if (held is null)
            {
                continue; // a download or clean-up is in progress elsewhere
            }

            foreach (var file in Directory.EnumerateFiles(entry.Directory))
            {
                var isTemporary = file.EndsWith(".partial", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal);
                if (isTemporary && now - File.GetLastWriteTimeUtc(file) > OrphanAge)
                {
                    FileLocks.TryDelete(file);
                }
            }

            var orphanData = entry.ReadMeta() is null && File.Exists(entry.DataPath)
                && now - File.GetLastWriteTimeUtc(entry.DataPath) > OrphanAge;
            if (entry.IsMarkedInvalid || orphanData)
            {
                entry.TryRemove();
            }
        }

        if (maxBytes is { } limit)
        {
            EnforceLimit(limit, keep);
        }
    }

    public void RemoveOtherVersions(CacheEntry keep)
    {
        var parent = Path.GetDirectoryName(keep.Directory)!;
        foreach (var entry in Entries().Where(entry => !IsSame(entry, keep)
                     && string.Equals(Path.GetDirectoryName(entry.Directory), parent, StringComparison.OrdinalIgnoreCase)))
        {
            using var held = FileLocks.TryAcquire(entry.LockPath);
            if (held is not null)
            {
                entry.TryRemove();
            }
        }
    }

    /// <summary>
    /// Removes every entry nobody is using, then its lock file, then every empty folder. Lock files
    /// are only deleted here: pruning keeps them, because a lock file that disappears while another
    /// process waits on it could let two processes believe they hold the same lock.
    /// </summary>
    public void Clear()
    {
        foreach (var entry in Entries())
        {
            bool removed;
            using (var held = FileLocks.TryAcquire(entry.LockPath))
            {
                removed = held is not null && entry.TryRemove();
            }

            if (removed)
            {
                FileLocks.TryDelete(entry.LockPath);
            }
        }

        RemoveEmptyDirectories(Root);
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var child in new DirectoryInfo(directory).EnumerateDirectories().Where(child => !IsLink(child)).Select(child => child.FullName).ToList())
        {
            RemoveEmptyDirectories(child);
            if (!Directory.EnumerateFileSystemEntries(child).Any())
            {
                try
                {
                    Directory.Delete(child);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private void EnforceLimit(long limit, CacheEntry? keep)
    {
        var sized = Entries()
            .Select(entry => (Entry: entry, Meta: entry.ReadMeta(), Size: File.Exists(entry.DataPath) ? new FileInfo(entry.DataPath).Length : 0))
            .Where(item => item.Meta is not null && item.Size > 0)
            .ToList();
        var total = sized.Sum(item => item.Size);
        foreach (var item in sized.OrderBy(item => item.Meta!.LastUsedUtc))
        {
            if (total <= limit)
            {
                break;
            }

            if (IsSame(item.Entry, keep))
            {
                continue;
            }

            using var held = FileLocks.TryAcquire(item.Entry.LockPath);
            if (held is not null && item.Entry.TryRemove())
            {
                total -= item.Size;
            }
        }
    }

    private static bool IsSame(CacheEntry entry, CacheEntry? other) =>
        other is not null && string.Equals(entry.Directory, other.Directory, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Downloads without a persistent cache go each to a folder of its own, <c>DbSus-&lt;32 hex digits&gt;</c>,
/// created directly in the system temporary directory, with no parent shared between users or
/// processes: on Linux and macOS, the sticky bit of <c>/tmp</c> then keeps other users from renaming
/// or deleting it. The folder is created only if nothing has that name; on the <c>net10.0</c> build
/// outside Windows it is readable by its owner only (0700), and elsewhere it has the system's
/// default permissions (usually 0755 on Linux). Folders left behind (older than 24 hours, lock free,
/// files not open) are removed later, and so, for one more version, are those of the folder that
/// earlier versions used, <c>DbSus/downloads/&lt;32 hex digits&gt;</c>.
/// </summary>
internal sealed class TempStore(string temporaryDirectory, Func<DateTime> utcNow)
{
    internal const string FolderPrefix = "DbSus-";

    private const string LockFileName = "entry.lock";

    public static string DefaultDirectory => Path.GetTempPath();

    // The folder of version 0.1.0-preview.2 and earlier, cleaned for one more version.
    private string LegacyParent => Path.Combine(temporaryDirectory, "DbSus");

    private string LegacyRoot => Path.Combine(LegacyParent, "downloads");

    public CacheEntry Create(string fileName)
    {
        var directory = Path.Combine(temporaryDirectory, FolderPrefix + Guid.NewGuid().ToString("N"));
        CreateExclusive(directory);
        return new CacheEntry(directory, fileName);
    }

    /// <summary>
    /// Creates a folder that nothing has the name of yet, and fails otherwise: an existing folder,
    /// file or link of that name is never reused. With a random name, no one can create it first.
    /// </summary>
    internal static void CreateExclusive(string directory)
    {
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            throw new IOException($"The temporary folder '{directory}' already exists; it is not reused.");
        }

#if NET
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }
#else
        Directory.CreateDirectory(directory);
#endif

        if (CacheStore.IsLink(new DirectoryInfo(directory)))
        {
            throw new IOException($"The temporary folder '{directory}' is a link; it is not used.");
        }
    }

    /// <summary>
    /// Removes, best effort, the lock file and the folder of one download, once its lock file is all
    /// that is left in it; otherwise (a file still there, for example one not yet deleted by the
    /// system) leaves both for a later clean-up. Nothing else uses that folder, so no other download
    /// can be waiting on its lock.
    /// </summary>
    internal static void RemoveDownloadFolder(string directory)
    {
        try
        {
            var lockPath = Path.Combine(directory, LockFileName);
            if (Directory.EnumerateFileSystemEntries(directory).Any(entry => !string.Equals(entry, lockPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            File.Delete(lockPath);
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes the folders of downloads left behind. A failure on one folder (another user's, a lock
    /// that cannot be opened, a file in use) leaves that folder as it is and goes on to the next one;
    /// nothing is ever thrown.
    /// </summary>
    public void CleanOrphans()
    {
        var now = utcNow();
        foreach (var directory in Candidates())
        {
            try
            {
                CleanOrphan(directory, now);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        RemoveIfEmpty(LegacyRoot);
        RemoveIfEmpty(LegacyParent);
    }

    // Folders named as this class names them: DbSus-<32 hex> in the temporary directory and, from
    // earlier versions, <32 hex> in DbSus/downloads (unless DbSus or downloads is a link).
    private List<string> Candidates()
    {
        var found = new List<string>();
        try
        {
            if (Directory.Exists(temporaryDirectory))
            {
                found.AddRange(Directory.EnumerateDirectories(temporaryDirectory, FolderPrefix + "*")
                    .Where(directory => IsHex32(Path.GetFileName(directory).Substring(FolderPrefix.Length))));
            }

            if (Directory.Exists(LegacyRoot)
                && !CacheStore.IsLink(new DirectoryInfo(LegacyParent))
                && !CacheStore.IsLink(new DirectoryInfo(LegacyRoot)))
            {
                found.AddRange(Directory.EnumerateDirectories(LegacyRoot).Where(directory => IsHex32(Path.GetFileName(directory))));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return found;
    }

    // Removes a folder of a download only when everything in it is recognized: no link, no
    // subfolder, its lock file, and at most the downloaded file and its .partial, none of them open;
    // or nothing at all (a folder whose lock file was deleted and the folder not, see
    // RemoveDownloadFolder).
    private static void CleanOrphan(string directory, DateTime now)
    {
        var folder = new DirectoryInfo(directory);
        if (CacheStore.IsLink(folder) || now - folder.LastWriteTimeUtc <= CacheStore.OrphanAge)
        {
            return;
        }

        if (!folder.EnumerateFileSystemInfos().Any())
        {
            folder.Delete();
            return;
        }

        var lockPath = Path.Combine(directory, LockFileName);
        var files = folder.EnumerateFiles().ToList();
        if (!File.Exists(lockPath) || folder.EnumerateDirectories().Any() || files.Any(file => (file.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            return;
        }

        var others = files.Select(file => file.Name).Where(name => !string.Equals(name, LockFileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!IsOneDownload(others))
        {
            return;
        }

        using (var held = FileLocks.TryAcquire(lockPath))
        {
            if (held is null)
            {
                return;
            }

            foreach (var name in others)
            {
                if (!FileLocks.TryDeleteIfUnused(Path.Combine(directory, name)))
                {
                    return;
                }
            }
        }

        RemoveDownloadFolder(directory);
    }

    // The files of one download: none, the file, its .partial, or both.
    private static bool IsOneDownload(List<string> names) => names.Count switch
    {
        0 or 1 => true,
        2 => string.Equals(names[0] + ".partial", names[1], StringComparison.OrdinalIgnoreCase)
             || string.Equals(names[1] + ".partial", names[0], StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsHex32(string name) => name.Length == 32 && name.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void RemoveIfEmpty(string directory)
    {
        try
        {
            var folder = new DirectoryInfo(directory);
            if (folder.Exists && !CacheStore.IsLink(folder) && !folder.EnumerateFileSystemInfos().Any())
            {
                folder.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
