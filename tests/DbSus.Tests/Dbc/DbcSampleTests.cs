using System.Security.Cryptography;
using System.Text.Json;
using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

/// <summary>
/// Real DATASUS files, compared with the hashes the oracle recorded in tests/data/expected.json.
/// The files are never committed: run tools/fetch-samples to download the pinned ones into
/// tests/data/local/. Each test skips itself when its file is absent or is a different version.
/// </summary>
public class DbcSampleTests
{
    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var entry in LoadExpected())
        {
            data.Add(entry.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void OpenAsDbf_RealSample_MatchesOracleAndDbfInvariants(string file)
    {
        var expected = LoadExpected().Single(entry => entry.File == file);
        var path = Path.Combine(RepositoryPaths.LocalSamples, file);
        if (!File.Exists(path))
        {
            Assert.Skip($"{file} is not in tests/data/local; run tools/fetch-samples.");
        }

        if (Sha256OfFile(path) != expected.DbcSha256)
        {
            Assert.Skip($"{file} in tests/data/local is not the version pinned in expected.json.");
        }

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = DbcFile.OpenAsDbf(path);
        var buffer = new byte[81920];
        var header = new byte[12];
        long size = 0;
        byte lastByte = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (size < header.Length)
            {
                Array.Copy(buffer, 0, header, size, Math.Min(read, header.Length - (int)size));
            }

            sha256.AppendData(buffer, 0, read);
            size += read;
            lastByte = buffer[read - 1];
        }

        Assert.Equal(expected.DbfSize, size);
        Assert.Equal(expected.DbfSha256, ToHex(sha256.GetHashAndReset()));

        // Independent of the oracle: header + records, plus an optional 0x1A end-of-file marker.
        var records = BitConverter.ToUInt32(header, 4);
        var headerLength = BitConverter.ToUInt16(header, 8);
        var recordLength = BitConverter.ToUInt16(header, 10);
        var tableSize = headerLength + ((long)records * recordLength);
        Assert.True(
            size == tableSize || (size == tableSize + 1 && lastByte == 0x1A),
            $"DBF size {size} does not match header {headerLength} + {records} records x {recordLength} (+ optional 0x1A).");
    }

    private static string Sha256OfFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return ToHex(sha256.ComputeHash(stream));
    }

    private static string ToHex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));

    private static List<ExpectedSample> LoadExpected()
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

    // A class rather than a record: records need IsExternalInit, which net481 lacks.
    private sealed class ExpectedSample(string file, string dbcSha256, string dbfSha256, long dbfSize)
    {
        public string File { get; } = file;

        public string DbcSha256 { get; } = dbcSha256;

        public string DbfSha256 { get; } = dbfSha256;

        public long DbfSize { get; } = dbfSize;
    }
}
