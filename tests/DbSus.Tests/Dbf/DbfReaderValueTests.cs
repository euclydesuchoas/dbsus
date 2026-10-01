using System.Text;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderValueTests
{
    [Fact]
    public void GetString_TrimsTrailingSpacesOnly()
    {
        using var reader = SingleValue('C', 10, "  AB C");

        Assert.Equal("  AB C", reader.GetString(0));
        Assert.Equal("  AB C", reader.GetValue(0));
    }

    [Fact]
    public void GetValue_BlankString_IsEmptyByDefault()
    {
        using var reader = SingleValue('C', 5, "     ");

        Assert.False(reader.IsDBNull(0));
        Assert.Equal(string.Empty, reader.GetValue(0));
    }

    [Fact]
    public void GetValue_BlankStringWithBlankStringsAsNull_IsDBNull()
    {
        using var reader = SingleValue('C', 5, "     ", new DbfReadOptions { BlankStringsAsNull = true });

        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
        Assert.Null(reader.GetFieldValue<string>(0));
    }

    [Fact]
    public void GetValue_ZerosInCharacterField_AreKeptAsText()
    {
        using var reader = SingleValue('C', 4, "0000", new DbfReadOptions { BlankStringsAsNull = true });

        Assert.Equal("0000", reader.GetValue(0));
    }

    [Theory]
    [InlineData("   12.50", 12.50)]
    [InlineData("   -3.25", -3.25)]
    [InlineData("   +7.00", 7.00)]
    [InlineData("    0.00", 0)]
    [InlineData("12345.67", 12345.67)]
    public void GetDecimal_PaddedNumbers_ParseInvariantly(string stored, double expected)
    {
        using var reader = SingleValue('N', 8, stored, decimals: 2);

        Assert.Equal((decimal)expected, reader.GetDecimal(0));
        Assert.Equal((decimal)expected, reader.GetValue(0));
    }

    [Fact]
    public void GetValue_TwentyDigitNumber_UsesDecimal()
    {
        using var reader = SingleValue('N', 20, "12345678901234567890");

        Assert.Equal(12345678901234567890m, reader.GetValue(0));
    }

    [Theory]
    [InlineData('N', 8, 2)]
    [InlineData('N', 6, 0)]
    [InlineData('D', 8, 0)]
    [InlineData('L', 1, 0)]
    public void GetValue_BlankNonCharacterField_IsDBNull(char type, int length, int decimals)
    {
        using var reader = SingleValue(type, length, new string(' ', length), decimals: decimals);

        Assert.True(reader.IsDBNull(0));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
    }

    [Theory]
    [InlineData("  ***.**")]
    [InlineData("   1,50 ")]
    [InlineData("   1.2.3")]
    [InlineData("    -   ")]
    public void GetValue_InvalidNumber_ThrowsFormatExceptionWithoutTheValue(string stored)
    {
        using var reader = SingleValue('N', 8, stored, decimals: 2);

        var exception = Assert.Throws<FormatException>(() => reader.GetValue(0));

        Assert.Contains("Field F", exception.Message, StringComparison.Ordinal);
        Assert.Contains("record 1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Trim(), exception.Message, StringComparison.Ordinal);
        Assert.False(reader.IsDBNull(0));
    }

    [Fact]
    public void GetDateTime_YyyymmddDate_ParsesAsDateOnly()
    {
        using var reader = SingleValue('D', 8, "20240229");

        Assert.Equal(new DateTime(2024, 2, 29), reader.GetDateTime(0));
        Assert.Equal(DateTimeKind.Unspecified, reader.GetDateTime(0).Kind);
    }

    [Fact]
    public void GetValue_AllZerosDate_IsDBNull()
    {
        using var reader = SingleValue('D', 8, "00000000");

        Assert.True(reader.IsDBNull(0));
    }

    [Theory]
    [InlineData("20230229")]
    [InlineData("20241301")]
    [InlineData("2024013X")]
    [InlineData("31012024")]
    public void GetValue_InvalidDate_ThrowsFormatException(string stored)
    {
        using var reader = SingleValue('D', 8, stored);

        Assert.Throws<FormatException>(() => reader.GetValue(0));
    }

    [Theory]
    [InlineData("T", true)]
    [InlineData("t", true)]
    [InlineData("Y", true)]
    [InlineData("y", true)]
    [InlineData("F", false)]
    [InlineData("f", false)]
    [InlineData("N", false)]
    [InlineData("n", false)]
    public void GetBoolean_LogicalCodes_Parse(string stored, bool expected)
    {
        using var reader = SingleValue('L', 1, stored);

        Assert.Equal(expected, reader.GetBoolean(0));
    }

    [Fact]
    public void GetValue_UnknownLogical_IsDBNull()
    {
        using var reader = SingleValue('L', 1, "?");

        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public void NumericGetters_ConvertWithinTheNumericFamilyWhenTheValueFits()
    {
        var dbf = new DbfBuilder()
            .Field("SMALL", 'N', 5)
            .Field("WIDE", 'N', 12)
            .Field("MONEY", 'N', 8, 2)
            .Field("ROUND", 'N', 8, 2)
            .Record("  123", "        4567", "   12.50", "    5.00")
            .Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Equal(123L, reader.GetInt64(0));
        Assert.Equal(123m, reader.GetDecimal(0));
        Assert.Equal(123d, reader.GetDouble(0));
        Assert.Equal((short)123, reader.GetInt16(0));
        Assert.Equal(4567, reader.GetInt32(1));
        Assert.Equal(12.5d, reader.GetDouble(2));
        Assert.Equal(5, reader.GetInt32(3));
        Assert.Throws<InvalidCastException>(() => reader.GetInt32(2));
        Assert.Throws<OverflowException>(() => reader.GetByte(1));
    }

    [Fact]
    public void TypedGetters_OnAnotherFieldType_ThrowInvalidCastException()
    {
        var dbf = new DbfBuilder()
            .Field("CODE", 'C', 6)
            .Field("QTY", 'N', 5)
            .Field("WHEN", 'D', 8)
            .Record("000123", "    1", "20240101")
            .Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Throws<InvalidCastException>(() => reader.GetInt32(0));
        Assert.Throws<InvalidCastException>(() => reader.GetDecimal(2));
        Assert.Throws<InvalidCastException>(() => reader.GetDateTime(1));
        Assert.Throws<InvalidCastException>(() => reader.GetString(1));
        Assert.Throws<InvalidCastException>(() => reader.GetBoolean(0));
    }

    [Fact]
    public void GetFieldValue_NullValue_ReturnsNullForNullableAndThrowsOtherwise()
    {
        using var reader = SingleValue('N', 5, "     ");

        Assert.Null(reader.GetFieldValue<int?>(0));
        Assert.Null(reader.GetFieldValue<decimal?>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetFieldValue<int>(0));
        Assert.Throws<InvalidCastException>(() => reader.GetInt32(0));
    }

    [Fact]
    public void GetFieldValue_Values_ConvertLikeTheTypedGetters()
    {
        using var reader = SingleValue('N', 5, "   42");

        Assert.Equal(42, reader.GetFieldValue<int>(0));
        Assert.Equal(42L, reader.GetFieldValue<long?>(0));
        Assert.Equal(42m, reader.GetFieldValue<decimal>(0));
        Assert.Equal((object)42, reader.GetFieldValue<object>(0));
    }

    [Fact]
    public void GetRawBytes_ReturnsStoredBytesIncludingPadding()
    {
        var dbf = new DbfBuilder().Field("CNS", 'C', 6).Record(new byte[] { 0x80, 0x9F, 0x41 }).Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Equal(new byte[] { 0x80, 0x9F, 0x41, 0x20, 0x20, 0x20 }, reader.GetRawBytes(0).ToArray());
        Assert.Equal(new byte[] { 0x80, 0x9F, 0x41, 0x20, 0x20, 0x20 }, reader.GetRawBytes("cns").ToArray());
        var buffer = new byte[2];
        Assert.Equal(2, reader.GetBytes(0, 1, buffer, 0, 2));
        Assert.Equal(new byte[] { 0x9F, 0x41 }, buffer);
        Assert.Equal(6, reader.GetBytes(0, 0, null, 0, 0));
    }

    [Theory]
    [InlineData(0x02, 0x87, 'ç')] // code page 850
    [InlineData(0x03, 0xE7, 'ç')] // code page 1252
    [InlineData(0x58, 0xBA, 'º')] // code page 1252 (dBASE WEurope ANSI)
    [InlineData(0x57, 0xE3, 'ã')] // ISO-8859-1
    [InlineData(0x00, 0x80, '€')] // no LDID: fallback to code page 1252
    public void GetString_LanguageDriverId_SelectsCodePage(byte languageDriverId, byte stored, char expected)
    {
        var dbf = new DbfBuilder { LanguageDriverId = languageDriverId }.Field("TEXT", 'C', 1).Record(new[] { stored }).Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Equal(expected.ToString(), reader.GetString(0));
    }

    [Fact]
    public void GetString_EncodingOption_OverridesLanguageDriverId()
    {
        var dbf = new DbfBuilder { LanguageDriverId = 0x03 }.Field("TEXT", 'C', 1).Record(new byte[] { 0x87 }).Build();
        var cp850 = Cp850();
        using var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { Encoding = cp850 });
        Assert.True(reader.Read());

        Assert.Equal("ç", reader.GetString(0));
        Assert.Equal(850, reader.Encoding.CodePage);
    }

    [Fact]
    public void GetString_FallbackEncoding_AppliesOnlyWithoutKnownLanguageDriverId()
    {
        var cp850 = Cp850();
        var withoutLdid = new DbfBuilder { LanguageDriverId = 0x00 }.Field("TEXT", 'C', 1).Record(new byte[] { 0x87 }).Build();
        var withLdid = new DbfBuilder { LanguageDriverId = 0x03 }.Field("TEXT", 'C', 1).Record(new byte[] { 0xE7 }).Build();

        using var fallback = DbfReader.Open(new MemoryStream(withoutLdid), new DbfReadOptions { FallbackEncoding = cp850 });
        using var declared = DbfReader.Open(new MemoryStream(withLdid), new DbfReadOptions { FallbackEncoding = cp850 });

        Assert.Equal(850, fallback.Encoding.CodePage);
        Assert.Equal(1252, declared.Encoding.CodePage);
    }

#if NETFRAMEWORK
    private static Encoding Cp850() => Encoding.GetEncoding(850);
#else
    private static Encoding Cp850() => CodePagesEncodingProvider.Instance.GetEncoding(850)!;
#endif

    private static DbfReader SingleValue(char type, int length, string stored, DbfReadOptions? options = null, int decimals = 0)
    {
        var dbf = new DbfBuilder().Field("F", type, length, decimals).Record(stored).Build();
        var reader = DbfReader.Open(new MemoryStream(dbf), options);
        Assert.True(reader.Read());
        return reader;
    }
}
