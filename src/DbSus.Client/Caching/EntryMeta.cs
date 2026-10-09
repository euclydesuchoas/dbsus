using System.Globalization;
using System.Text;

namespace DbSus.Client.Caching;

/// <summary>
/// Metadata of a cached file, stored next to it as plain <c>key=value</c> lines (no JSON
/// dependency). Unknown keys are ignored; a missing or malformed value makes the entry invalid.
/// </summary>
internal sealed class EntryMeta
{
    private const int CurrentFormat = 1;
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public EntryMeta(string path, long size, DateTime modifiedUtc, DateTime downloadedUtc, DateTime lastUsedUtc, bool crcVerified)
    {
        Path = path;
        Size = size;
        ModifiedUtc = modifiedUtc;
        DownloadedUtc = downloadedUtc;
        LastUsedUtc = lastUsedUtc;
        CrcVerified = crcVerified;
    }

    public string Path { get; }

    public long Size { get; }

    public DateTime ModifiedUtc { get; }

    public DateTime DownloadedUtc { get; }

    public DateTime LastUsedUtc { get; }

    public bool CrcVerified { get; }

    public EntryMeta WithLastUsed(DateTime lastUsedUtc) => new(Path, Size, ModifiedUtc, DownloadedUtc, lastUsedUtc, CrcVerified);

    public static EntryMeta? TryRead(string file)
    {
        try
        {
            return File.Exists(file) ? TryParse(File.ReadAllText(file, Encoding.UTF8)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static EntryMeta? TryParse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split(['\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).TrimEnd('\r');
            }
        }

        if (!values.TryGetValue("format", out var format) || format != CurrentFormat.ToString(CultureInfo.InvariantCulture)
            || !values.TryGetValue("path", out var path) || path.Length == 0
            || !TryLong(values, "size", out var size)
            || !TryTime(values, "modifiedUtc", out var modified)
            || !TryTime(values, "downloadedUtc", out var downloaded)
            || !TryTime(values, "lastUsedUtc", out var lastUsed)
            || !values.TryGetValue("crcVerified", out var verified) || verified is not ("true" or "false"))
        {
            return null;
        }

        return new EntryMeta(path, size, modified, downloaded, lastUsed, verified == "true");
    }

    public string Serialize()
    {
        var text = new StringBuilder();
        text.Append("format=").Append(CurrentFormat.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("path=").Append(Path).Append('\n');
        text.Append("size=").Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("modifiedUtc=").Append(ModifiedUtc.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
        text.Append("downloadedUtc=").Append(DownloadedUtc.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
        text.Append("lastUsedUtc=").Append(LastUsedUtc.ToString(TimeFormat, CultureInfo.InvariantCulture)).Append('\n');
        text.Append("crcVerified=").Append(CrcVerified ? "true" : "false").Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// Writes to <c>entry.meta.tmp</c> in the same folder and moves it over the target. Every caller
    /// holds the entry's lock, so one fixed temporary name is enough: a temporary file left by a
    /// process that stopped is overwritten by the next write (or pruned after 24 hours). The short
    /// name keeps the download's <c>.partial</c> the longest path of an entry, for file names of
    /// seven characters or more, so a path that is too long fails before any data is transferred.
    /// </summary>
    public void WriteAtomically(string file)
    {
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, Serialize(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        FileMoves.Replace(temporary, file);
    }

    private static bool TryLong(Dictionary<string, string> values, string key, out long value)
    {
        value = 0;
        return values.TryGetValue(key, out var text)
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryTime(Dictionary<string, string> values, string key, out DateTime value)
    {
        value = default;
        if (!values.TryGetValue(key, out var text)
            || !DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out value))
        {
            return false;
        }

        value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return true;
    }
}

internal static class FileMoves
{
    /// <summary>Moves <paramref name="source"/> over <paramref name="destination"/>, replacing it.</summary>
    public static void Replace(string source, string destination)
    {
#if NET
        File.Move(source, destination, overwrite: true);
#else
        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(source, destination);
        }
#endif
    }
}
