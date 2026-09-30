using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DbSus.Tests.TestSupport;

/// <summary>Reads tests/data/expected.json, the oracle hashes for the pinned DBC samples.</summary>
internal static class ExpectedSamples
{
    public static IReadOnlyList<ExpectedSample> All { get; } = Load();

    public static ExpectedSample Get(string file) => All.Single(entry => entry.File == file);

    public static string Sha256Hex(byte[] hash) =>
        string.Concat(hash.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    public static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Sha256Hex(sha256.ComputeHash(stream));
    }

    private static List<ExpectedSample> Load()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryPaths.ExpectedHashes));
        return
        [
            .. document.RootElement.GetProperty("files").EnumerateArray().Select(entry => new ExpectedSample(
                entry.GetProperty("file").GetString()!,
                entry.GetProperty("dbcSha256").GetString()!,
                entry.GetProperty("dbfSha256").GetString()!,
                entry.GetProperty("dbfSize").GetInt64())),
        ];
    }
}

// A class rather than a record: records need IsExternalInit, which net481 lacks.
internal sealed class ExpectedSample(string file, string dbcSha256, string dbfSha256, long dbfSize)
{
    public string File { get; } = file;

    public string DbcSha256 { get; } = dbcSha256;

    public string DbfSha256 { get; } = dbfSha256;

    public long DbfSize { get; } = dbfSize;
}
