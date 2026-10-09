using DbSus.Client.Caching;
using Xunit;

namespace DbSus.Client.Tests;

public class EntryMetaTests
{
    private static readonly EntryMeta Sample = new(
        "/SIASUS/200801_/Dados/PAMG2407a.dbc",
        183_994_941,
        new DateTime(2025, 8, 8, 12, 34, 56, DateTimeKind.Utc),
        new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 30, 11, 0, 0, 123, DateTimeKind.Utc),
        crcVerified: true);

    [Fact]
    public void Serialize_ThenParse_RoundTrips()
    {
        var parsed = EntryMeta.TryParse(Sample.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(Sample.Path, parsed.Path);
        Assert.Equal(Sample.Size, parsed.Size);
        Assert.Equal(Sample.ModifiedUtc, parsed.ModifiedUtc);
        Assert.Equal(Sample.LastUsedUtc, parsed.LastUsedUtc);
        Assert.Equal(DateTimeKind.Utc, parsed.LastUsedUtc.Kind);
        Assert.True(parsed.CrcVerified);
    }

    [Fact]
    public void Serialize_IsPlainKeyValueText()
    {
        var text = Sample.Serialize();

        Assert.StartsWith("format=1\n", text, StringComparison.Ordinal);
        Assert.Contains("size=183994941\n", text, StringComparison.Ordinal);
        Assert.Contains("modifiedUtc=2025-08-08T12:34:56.0000000Z\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_UnknownKeysAndWindowsLineEndings_AreAccepted()
    {
        var text = Sample.Serialize().Replace("\n", "\r\n") + "futureKey=whatever\r\n";

        Assert.NotNull(EntryMeta.TryParse(text));
    }

    [Theory]
    [InlineData("format=2")]
    [InlineData("size=-1")]
    [InlineData("size=abc")]
    [InlineData("modifiedUtc=yesterday")]
    [InlineData("crcVerified=yes")]
    [InlineData("path=")]
    public void TryParse_MalformedValue_ReturnsNull(string replacement)
    {
        var key = replacement.Substring(0, replacement.IndexOf('=') + 1);
        var lines = Sample.Serialize().Split('\n').Select(line => line.StartsWith(key, StringComparison.Ordinal) ? replacement : line);

        Assert.Null(EntryMeta.TryParse(string.Join("\n", lines)));
    }

    [Fact]
    public void WriteAtomically_OrphanTemporaryFromAnEarlierWrite_IsOverwritten()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dbsus-entry-meta-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var meta = Path.Combine(directory, "entry.meta");
            File.WriteAllText(meta + ".tmp", "a longer half-written text left by a process that stopped while writing");

            Sample.WriteAtomically(meta);

            Assert.Equal(["entry.meta"], Directory.GetFiles(directory).Select(Path.GetFileName));
            Assert.Equal(Sample.Serialize(), File.ReadAllText(meta));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryParse_MissingKey_ReturnsNull()
    {
        var lines = Sample.Serialize().Split('\n').Where(line => !line.StartsWith("lastUsedUtc=", StringComparison.Ordinal));

        Assert.Null(EntryMeta.TryParse(string.Join("\n", lines)));
    }
}
