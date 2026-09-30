using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderHeaderTests
{
    [Fact]
    public void Open_TypicalHeader_ExposesTableAndFields()
    {
        var dbf = new DbfBuilder { LanguageDriverId = 0x02 }
            .Field("UF_ZI", 'C', 6)
            .Field("VAL_TOT", 'N', 14, 2)
            .Record("140000", "        427.95")
            .Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.Equal(0x03, reader.Header.Version);
        Assert.Equal(new DateTime(2026, 9, 30), reader.Header.LastUpdate);
        Assert.Equal(1, reader.Header.RecordCount);
        Assert.Equal(32 + (2 * 32) + 1, reader.Header.HeaderLength);
        Assert.Equal(1 + 6 + 14, reader.Header.RecordLength);
        Assert.Equal(0x02, reader.Header.LanguageDriverId);
        Assert.Equal(0, reader.Header.UnusedRecordBytes);
        Assert.Collection(
            reader.Header.Fields,
            field => Assert.Equal(("UF_ZI", 'C', 6, 0, 0), (field.Name, field.TypeCode, field.Length, field.DecimalCount, field.Index)),
            field => Assert.Equal(("VAL_TOT", 'N', 14, 2, 1), (field.Name, field.TypeCode, field.Length, field.DecimalCount, field.Index)));
    }

    [Fact]
    public void Open_ZeroInsteadOfFieldTerminator_ReadsAllFields()
    {
        // Shape of CNES STRR2301: descriptors fill the header and the last byte is 0x00.
        var dbf = new DbfBuilder { Terminator = 0x00 }
            .Field("CNES", 'C', 7)
            .Field("NAT_JUR", 'C', 4)
            .Record("1234567", "1023")
            .Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Equal(["CNES", "NAT_JUR"], reader.Fields.Select(field => field.Name));
        Assert.Equal("1023", reader.GetString("NAT_JUR"));
    }

    [Fact]
    public void Open_ExtraBytesAfterTerminator_SkipsThem()
    {
        var dbf = new DbfBuilder { ExtraHeaderBytes = 263 }.Field("CODE", 'C', 3).Record("ABC").Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Single(reader.Fields);
        Assert.Equal("ABC", reader.GetString(0));
    }

    [Fact]
    public void Open_RecordLongerThanFields_ToleratesAndExposesUnusedBytes()
    {
        var dbf = new DbfBuilder { ExtraRecordBytes = 3 }.Field("CODE", 'C', 3).Record("ABC").Record("DEF").Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.Equal(3, reader.Header.UnusedRecordBytes);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("DEF", reader.GetString(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Open_FieldsLongerThanRecord_ThrowsInvalidDataException()
    {
        var dbf = new DbfBuilder { DeclaredRecordLength = 3 }.Field("CODE", 'C', 3).Build();

        var exception = Assert.Throws<InvalidDataException>(() => DbfReader.Open(new MemoryStream(dbf)));

        Assert.Contains("record length is 3", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public void Open_TruncatedHeader_ThrowsInvalidDataException(int length)
    {
        var dbf = new DbfBuilder().Field("CODE", 'C', 3).Field("NAME", 'C', 5).Build();

        Assert.Throws<InvalidDataException>(() => DbfReader.Open(new MemoryStream(dbf, 0, length)));
    }

    [Fact]
    public void Open_InvalidLastUpdateBytes_ExposesNullDate()
    {
        var dbf = new DbfBuilder { LastUpdate = [126, 13, 40] }.Field("CODE", 'C', 3).Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.Null(reader.Header.LastUpdate);
    }

    [Theory]
    [InlineData('C', 10, 0, typeof(string))]
    [InlineData('N', 9, 0, typeof(int))]
    [InlineData('N', 10, 0, typeof(long))]
    [InlineData('N', 18, 0, typeof(long))]
    [InlineData('N', 19, 0, typeof(decimal))]
    [InlineData('N', 13, 2, typeof(decimal))]
    [InlineData('F', 10, 0, typeof(decimal))]
    [InlineData('D', 8, 0, typeof(DateTime))]
    [InlineData('L', 1, 0, typeof(bool))]
    [InlineData('M', 10, 0, typeof(byte[]))]
    public void GetFieldType_EachDbaseType_MapsToDocumentedClrType(char type, int length, int decimals, Type expected)
    {
        var dbf = new DbfBuilder().Field("F", type, length, decimals).Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.Equal(expected, reader.GetFieldType(0));
        Assert.Equal(expected, reader.Fields[0].FieldType);
        Assert.Equal(type.ToString(), reader.GetDataTypeName(0));
    }

    [Fact]
    public void UnsupportedField_IsFlaggedAndReadAsRawBytes()
    {
        var dbf = new DbfBuilder().Field("NOTES", 'M', 10).Record("0000000042").Build();

        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.False(reader.Fields[0].IsSupported);
        Assert.Equal("0000000042"u8.ToArray(), (byte[])reader.GetValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
    }
}
