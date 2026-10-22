using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

/// <summary>
/// The readers over a source that returns fewer bytes than asked, as a network stream does: the
/// committed CNES fixtures read through <see cref="DbSusFile.OpenDbcReader(Stream, DbfReadOptions?, DbcReadOptions?, bool)"/>,
/// alone and as the parts of a split table, give the same records and the same checksum outcome as
/// when each read returns all it is asked for. Field values are never printed: a difference is
/// reported by record and part only.
/// </summary>
public class DbcShortReadTests
{
    private static readonly int[] Seeds = [1, 2, 3, 4, 5];

    [Theory]
    [InlineData("EQRR0509.dbc", 1, 1)]
    [InlineData("EQRR0509.dbc", 1, 3)]
    [InlineData("LTRR0610.dbc", 1, 1)]
    [InlineData("LTRR0610.dbc", 1, 3)]
    [InlineData("EQRR0509.dbc", 13, 1)]
    [InlineData("EQRR0509.dbc", 13, 3)]
    [InlineData("LTRR0610.dbc", 13, 1)]
    [InlineData("LTRR0610.dbc", 13, 3)]
    public void Fixture_ShortReads_GiveTheSameRecordsAndChecksum(string file, int maxChunk, int parts)
    {
        var dbc = Fixture(file);
        var expected = Read([.. Enumerable.Repeat(dbc, parts)], maxChunk: null, seed: 0);
        Assert.Null(expected.Failure);
        Assert.NotEmpty(expected.Records);

        foreach (var seed in Seeds)
        {
            AssertSame(expected, Read([.. Enumerable.Repeat(dbc, parts)], maxChunk, seed), $"{file}, {parts} part(s), reads of at most {maxChunk} byte(s), seed {seed}");
        }
    }

    [Theory]
    [InlineData("EQRR0509.dbc", 1)]
    [InlineData("LTRR0610.dbc", 13)]
    public void CorruptChecksum_ShortReads_FailAtTheSamePointWithTheSameMismatch(string file, int maxChunk)
    {
        var dbc = Fixture(file);
        byte[][] parts = [dbc, Corrupt(dbc), dbc];
        var expected = Read(parts, maxChunk: null, seed: 0);
        Assert.IsType<InvalidDataException>(expected.Failure);
        var mismatch = Assert.Single(expected.Mismatches);
        Assert.Equal(1, mismatch.PartIndex);

        foreach (var seed in Seeds)
        {
            AssertSame(expected, Read(parts, maxChunk, seed), $"{file} with a corrupt second part, reads of at most {maxChunk} byte(s), seed {seed}");
        }
    }

    private static byte[] Fixture(string file)
    {
        var path = Path.Combine(RepositoryPaths.Fixtures, file);
        Assert.True(File.Exists(path), $"Committed fixture {file} is missing from tests/data/fixtures.");
        return File.ReadAllBytes(path);
    }

    // The stored CRC-32 follows the DBF header, whose length is the UInt16 at offset 8.
    private static byte[] Corrupt(byte[] dbc)
    {
        var copy = (byte[])dbc.Clone();
        copy[copy[8] | (copy[9] << 8)] ^= 0xFF;
        return copy;
    }

    private static Outcome Read(IReadOnlyList<byte[]> parts, int? maxChunk, int seed)
    {
        var outcome = new Outcome();
        var dbcOptions = new DbcReadOptions { ChecksumMismatch = mismatch => outcome.Mismatches.Add(new Mismatch(mismatch.PartIndex, mismatch.PartName, mismatch.ExpectedChecksum, mismatch.ActualChecksum)) };
        var dbfOptions = new DbfReadOptions { IncludeDeletedRecords = true };
        var streams = parts
            .Select((bytes, i) => maxChunk is { } max
                ? new ShortReadStream(new MemoryStream(bytes, writable: false), max, seed + i)
                : (Stream)new MemoryStream(bytes, writable: false))
            .ToList();
        using var reader = streams.Count == 1
            ? DbSusFile.OpenDbcReader(streams[0], dbfOptions, dbcOptions)
            : DbSusFile.OpenDbcReader(streams.Select((stream, i) => new DbcPart($"PART{(char)('a' + i)}.dbc", stream)), dbfOptions, dbcOptions);
        try
        {
            while (reader.Read())
            {
                var raw = new MemoryStream();
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    var bytes = reader.GetRawBytes(ordinal).ToArray();
                    raw.Write(bytes, 0, bytes.Length);
                }

                outcome.Records.Add(new Record(reader.RecordNumber, reader.PartIndex, reader.IsDeleted, raw.ToArray()));
            }
        }
        catch (InvalidDataException exception)
        {
            outcome.Failure = exception;
        }

        return outcome;
    }

    private static void AssertSame(Outcome expected, Outcome actual, string what)
    {
        Assert.True(expected.Records.Count == actual.Records.Count, $"{what}: {actual.Records.Count} records instead of {expected.Records.Count}.");
        for (var i = 0; i < expected.Records.Count; i++)
        {
            var (e, a) = (expected.Records[i], actual.Records[i]);
            Assert.True(
                e.Number == a.Number && e.Part == a.Part && e.Deleted == a.Deleted && e.Raw.AsSpan().SequenceEqual(a.Raw),
                $"{what}: record {i + 1} (number {e.Number}, part {e.Part}) differs.");
        }

        Assert.Equal(expected.Failure?.GetType(), actual.Failure?.GetType());
        Assert.Equal(expected.Failure?.Message, actual.Failure?.Message);
        Assert.Equal(expected.Mismatches, actual.Mismatches);
    }

    private sealed class Outcome
    {
        public List<Record> Records { get; } = [];

        public List<Mismatch> Mismatches { get; } = [];

        public Exception? Failure { get; set; }
    }

    private sealed class Record(long number, int part, bool deleted, byte[] raw)
    {
        public long Number { get; } = number;

        public int Part { get; } = part;

        public bool Deleted { get; } = deleted;

        public byte[] Raw { get; } = raw;
    }

    private sealed class Mismatch(int partIndex, string? partName, uint expected, uint actual) : IEquatable<Mismatch>
    {
        public int PartIndex { get; } = partIndex;

        public string? PartName { get; } = partName;

        public uint Expected { get; } = expected;

        public uint Actual { get; } = actual;

        public bool Equals(Mismatch? other) =>
            other is not null && PartIndex == other.PartIndex && PartName == other.PartName && Expected == other.Expected && Actual == other.Actual;

        public override bool Equals(object? obj) => Equals(obj as Mismatch);

        public override int GetHashCode() => PartIndex ^ (int)Expected ^ (int)Actual;

        public override string ToString() => $"part {PartIndex} ({PartName}): 0x{Expected:X8} / 0x{Actual:X8}";
    }
}
