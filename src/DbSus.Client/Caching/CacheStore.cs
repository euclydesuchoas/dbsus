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
            .. Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories)
                .Where(directory => VersionName.IsMatch(Path.GetFileName(directory)))
                .Select(directory => new CacheEntry(directory, Path.GetFileName(Path.GetDirectoryName(directory)!))),
        ];
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

    public void Clear()
    {
        foreach (var entry in Entries())
        {
            using var held = FileLocks.TryAcquire(entry.LockPath);
            if (held is not null)
            {
                entry.TryRemove();
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
/// Downloads without a persistent cache go to <c>%TEMP%/DbSus/downloads/&lt;id&gt;/</c>. Folders
/// left behind (older than 24 hours, lock free, files not open) are removed later.
/// </summary>
internal sealed class TempStore(string root, Func<DateTime> utcNow)
{
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "DbSus", "downloads");

    public CacheEntry Create(string fileName)
    {
        var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new CacheEntry(directory, fileName);
    }

    public void CleanOrphans()
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var now = utcNow();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (now - Directory.GetLastWriteTimeUtc(directory) <= CacheStore.OrphanAge)
            {
                continue;
            }

            var lockPath = Path.Combine(directory, "entry.lock");
            using (var held = FileLocks.TryAcquire(lockPath))
            {
                if (held is null)
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory).Where(file => !string.Equals(file, lockPath, StringComparison.OrdinalIgnoreCase)))
                {
                    FileLocks.TryDeleteIfUnused(file);
                }
            }

            if (Directory.EnumerateFiles(directory).All(file => string.Equals(file, lockPath, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
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
}
