using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfHeaderReadTests
{
    private static readonly byte[] Dbf = new DbfBuilder().Field("CODE", 'C', 2).Field("QTY", 'N', 4).Record("A1", "  12").Record("B2", "   7").Build();

    [Fact]
    public void Read_DbfStream_ReturnsHeaderAndStopsRightAfterIt()
    {
        var stream = new MemoryStream(Dbf);

        var header = DbfHeader.Read(stream);

        Assert.Equal(2, header.RecordCount);
        Assert.Equal(["CODE", "QTY"], header.Fields.Select(field => field.Name));
        Assert.Equal(header.HeaderLength, stream.Position);
    }

    [Fact]
    public void Read_DbcStream_ReadsTheUncompressedHeaderOnly()
    {
        var dbc = SyntheticDbc.BuildDbc(Dbf);
        var stream = new MemoryStream(dbc);

        var header = DbfHeader.Read(stream);

        Assert.Equal(2, header.RecordCount);
        Assert.Equal(header.HeaderLength, stream.Position);
    }

    [Fact]
    public void Read_NonSeekableOneByteStream_Works()
    {
        var header = DbfHeader.Read(new TrickleStream(Dbf));

        Assert.Equal(2, header.Fields.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(40)]
    public void Read_Truncated_ThrowsInvalidDataException(int length)
    {
        Assert.Throws<InvalidDataException>(() => DbfHeader.Read(new MemoryStream(Dbf, 0, length)));
    }

    [Fact]
    public void Read_RealFixture_GivesSchemaAndCountWithoutDecompressing()
    {
        using var file = File.OpenRead(Path.Combine(RepositoryPaths.Fixtures, "LTRR0610.dbc"));

        var header = DbfHeader.Read(file);

        Assert.Equal(102, header.RecordCount);
        Assert.Equal(27, header.Fields.Count);
        Assert.Equal(897, file.Position);
    }
}
