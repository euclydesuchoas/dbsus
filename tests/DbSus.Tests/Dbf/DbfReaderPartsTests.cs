using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public sealed class DbfReaderPartsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public DbfReaderPartsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Read_TwoParts_ReadsInOrderWithGlobalRecordNumbers()
    {
        using var reader = DbfReader.Open([Stream(Part("A1", "A2")), Stream(Part("B1", "B2", "B3"))]);

        Assert.Equal(2, reader.PartCount);
        Assert.Equal(5, reader.TotalRecordCount);
        var seen = new List<(string Code, long Number, int Part, long PartRecords)>();
        while (reader.Read())
        {
            seen.Add((reader.GetString(0), reader.RecordNumber, reader.PartIndex, reader.Header.RecordCount));
        }

        Assert.Equal(
            [("A1", 1, 0, 2), ("A2", 2, 0, 2), ("B1", 3, 1, 3), ("B2", 4, 1, 3), ("B3", 5, 1, 3)],
            seen);
    }

    [Fact]
    public void Read_DeletedRecordInLaterPart_IsSkippedButCounted()
    {
        var second = new DbfBuilder().Field("CODE", 'C', 2).DeletedRecord("B1").Record("B2").Build();
        using var reader = DbfReader.Open([Stream(Part("A1")), Stream(second)]);

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("B2", reader.GetString(0));
        Assert.Equal(3, reader.RecordNumber);
    }

    [Fact]
    public void Read_ProjectionAndConversions_ApplyToEveryPart()
    {
        var first = new DbfBuilder().Field("CODE", 'C', 2).Field("AGE", 'C', 3).Record("A1", "034").Build();
        var second = new DbfBuilder().Field("CODE", 'C', 2).Field("AGE", 'C', 3).Record("B1", "101").Build();
        var options = new DbfReadOptions { Columns = ["AGE"] };
        options.Conversions["AGE"] = DbfFieldConversion.Int32();
        using var reader = DbfReader.Open([Stream(first), Stream(second)], options);

        Assert.True(reader.Read());
        Assert.Equal(34, reader.GetInt32(0));
        Assert.True(reader.Read());
        Assert.Equal(101, reader.GetInt32(0));
    }

    [Fact]
    public void Open_DifferentWidth_ThrowsNamingPartAndField()
    {
        var second = new DbfBuilder().Field("CODE", 'C', 3).Record("B1").Build();

        var exception = Assert.Throws<InvalidDataException>(() => DbfReader.Open([Stream(Part("A1")), Stream(second)]));

        Assert.Equal("part 2 does not match part 1: field 1 is CODE C(2,0) in the first part and CODE C(3,0) here.", exception.Message);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("name")]
    [InlineData("type")]
    [InlineData("decimals")]
    [InlineData("recordLength")]
    [InlineData("codePage")]
    public void Open_SchemaDifference_ThrowsInvalidDataException(string difference)
    {
        var first = new DbfBuilder().Field("CODE", 'C', 2).Field("VAL", 'N', 6, 2).Record("A1", "  1.00").Build();
        var builder = new DbfBuilder { LanguageDriverId = difference == "codePage" ? (byte)0x02 : (byte)0x03 };
        builder.Field(difference == "name" ? "COD" : "CODE", difference == "type" ? 'N' : 'C', 2);
        builder.Field("VAL", 'N', 6, difference == "decimals" ? 1 : 2);
        if (difference == "count")
        {
            builder.Field("EXTRA", 'C', 1);
        }

        if (difference == "recordLength")
        {
            builder.ExtraRecordBytes = 1;
        }

        var second = builder.Build();

        var exception = Assert.Throws<InvalidDataException>(() => DbfReader.Open([Stream(first), Stream(second)]));

        Assert.StartsWith("part 2 does not match part 1: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_DifferentLanguageDriverWithEncodingOverride_IsAccepted()
    {
        var first = new DbfBuilder { LanguageDriverId = 0x03 }.Field("CODE", 'C', 2).Record("A1").Build();
        var second = new DbfBuilder { LanguageDriverId = 0x02 }.Field("CODE", 'C', 2).Record("B1").Build();
        var options = new DbfReadOptions { Encoding = System.Text.Encoding.ASCII };

        using var reader = DbfReader.Open([Stream(first), Stream(second)], options);

        Assert.Equal(2, reader.TotalRecordCount);
    }

    [Fact]
    public void Open_SchemaMismatch_DoesNotDisposeCallerStreams()
    {
        var first = new TrickleStream(Part("A1"));
        var second = new TrickleStream(new DbfBuilder().Field("CODE", 'C', 3).Record("B1").Build());

        Assert.Throws<InvalidDataException>(() => DbfReader.Open([first, second]));

        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public void Read_TruncatedLaterPart_NamesThePart()
    {
        var second = Part("B1", "B2");
        using var reader = DbfReader.Open([Stream(Part("A1")), new MemoryStream(second, 0, second.Length - 3)]);
        Assert.True(reader.Read());
        Assert.True(reader.Read());

        var exception = Assert.Throws<InvalidDataException>(() => reader.Read());

        Assert.StartsWith("part 2: ", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetValue_InvalidValueInLaterPart_NamesPartAndGlobalRecordButNotValue()
    {
        var first = new DbfBuilder().Field("QTY", 'N', 3).Record("  1").Build();
        var second = new DbfBuilder().Field("QTY", 'N', 3).Record("  2").Record("x9x").Build();
        using var reader = DbfReader.Open([Stream(first), Stream(second)]);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.True(reader.Read());

        var exception = Assert.Throws<FormatException>(() => reader.GetValue(0));

        Assert.Equal("Field QTY (N) of record 3 (part 2) does not hold a number.", exception.Message);
    }

    [Fact]
    public void Open_NoParts_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DbfReader.Open(Array.Empty<Stream>()));
        Assert.Throws<ArgumentException>(() => DbfReader.Open(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => DbfReader.Open(new Stream[] { Stream(Part("A1")), null! }));
    }

    [Fact]
    public void OpenDbcReader_Paths_ReleasesEachPartOnceItIsRead()
    {
        var first = WriteDbc("TESTE2407a.dbc", Part("A1"));
        var second = WriteDbc("TESTE2407b.dbc", Part("B1"));
        using var reader = DbSusFile.OpenDbcReader([first, second]);

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal(1, reader.PartIndex);
        File.Delete(first); // the first part's file is already closed
        Assert.False(reader.Read());
        File.Delete(second);
    }

    [Fact]
    public void OpenDbcReader_Paths_ChecksumMismatchNamesThePartAtItsEnd()
    {
        var first = WriteDbc("TESTE2407a.dbc", Part("A1"), checksum: 0);
        var second = WriteDbc("TESTE2407b.dbc", Part("B1"));
        using var reader = DbSusFile.OpenDbcReader([first, second]);
        Assert.True(reader.Read());

        var exception = Assert.Throws<InvalidDataException>(() => reader.Read());

        Assert.StartsWith("TESTE2407a.dbc: DBC checksum mismatch", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDbcReader_Paths_FailureOnLaterPartClosesEarlierFiles()
    {
        var first = WriteDbc("TESTE2407a.dbc", Part("A1"));
        var second = WriteDbc("TESTE2407b.dbc", new DbfBuilder().Field("CODE", 'C', 3).Record("B1").Build());

        Assert.Throws<InvalidDataException>(() => DbSusFile.OpenDbcReader([first, second]));

        File.Delete(first);
        File.Delete(second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenDbcReader_Streams_DisposeEveryStreamUnlessLeaveOpen(bool leaveOpen)
    {
        var first = new TrickleStream(SyntheticDbc.BuildDbc(Part("A1")));
        var second = new TrickleStream(SyntheticDbc.BuildDbc(Part("B1")));

        using (var reader = DbSusFile.OpenDbcReader([first, second], dbcOptions: new DbcReadOptions { LeaveOpen = leaveOpen }))
        {
            Assert.True(reader.Read());
        }

        Assert.Equal(!leaveOpen, first.IsDisposed);
        Assert.Equal(!leaveOpen, second.IsDisposed);
    }

    [Fact]
    public void OpenReader_Paths_MixesDbcAndDbfParts()
    {
        var first = WriteDbc("TESTE2407a.dbc", Part("A1"));
        var second = Path.Combine(_directory, "TESTE2407b.dbf");
        File.WriteAllBytes(second, Part("B1"));

        using var reader = DbSusFile.OpenReader([first, second]);

        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("B1", reader.GetString(0));
        Assert.False(reader.Read());
    }

    private static byte[] Part(params string[] codes)
    {
        var builder = new DbfBuilder().Field("CODE", 'C', 2);
        foreach (var code in codes)
        {
            builder.Record(code);
        }

        return builder.Build();
    }

    private static MemoryStream Stream(byte[] data) => new(data);

    private string WriteDbc(string name, byte[] dbf, uint? checksum = null)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, SyntheticDbc.BuildDbc(dbf, checksum));
        return path;
    }
}
