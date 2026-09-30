using System.Data.Common;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderConversionTests
{
    [Theory]
    [InlineData("yyyyMMdd", "20240131")]
    [InlineData("ddMMyyyy", "31012024")]
    public void Date_BothFormats_ParseToTheSameDate(string format, string stored)
    {
        using var reader = Converted('C', 8, stored, DbfFieldConversion.Date(format));

        Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
        Assert.Equal(new DateTime(2024, 1, 31), reader.GetDateTime(0));
        Assert.Equal(new DateTime(2024, 1, 31), reader.GetValue(0));
    }

    [Theory]
    [InlineData("        ")]
    [InlineData("00000000")]
    public void Date_BlankOrZeros_IsDBNull(string stored)
    {
        using var reader = Converted('C', 8, stored, DbfFieldConversion.Date("ddMMyyyy"));

        Assert.True(reader.IsDBNull(0));
        Assert.Null(reader.GetFieldValue<DateTime?>(0));
    }

    [Theory]
    [InlineData("ddMMyyyy", "20240131")] // year-first data read as day-first: month 24
    [InlineData("yyyyMMdd", "31012024")]
    [InlineData("ddMMyyyy", "3101202X")]
    public void Date_WrongLayoutOrText_ThrowsFormatExceptionWithoutTheValue(string format, string stored)
    {
        using var reader = Converted('C', 8, stored, DbfFieldConversion.Date(format));

        var exception = Assert.Throws<FormatException>(() => reader.GetValue(0));

        Assert.Contains(format == "ddMMyyyy" ? "DDMMYYYY" : "YYYYMMDD", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stored, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("034", 34)]
    [InlineData(" -12", -12)]
    [InlineData("+007", 7)]
    public void Int32_Digits_ParseIncludingLeadingZerosAndSign(string stored, int expected)
    {
        using var reader = Converted('C', 4, stored, DbfFieldConversion.Int32());

        Assert.Equal(typeof(int), reader.GetFieldType(0));
        Assert.Equal(expected, reader.GetInt32(0));
        Assert.Equal(expected, reader.GetValue(0));
        Assert.Equal(expected, reader.GetInt64(0));
    }

    [Theory]
    [InlineData("3000000000")] // does not fit in Int32
    [InlineData("12.5")]       // not a whole number
    [InlineData("1 234")]      // not a number
    public void Int32_ValueThatDoesNotFit_ThrowsFormatExceptionWithoutTheValue(string stored)
    {
        using var reader = Converted('C', 10, stored, DbfFieldConversion.Int32());

        var fromGetValue = Assert.Throws<FormatException>(() => reader.GetValue(0));
        var fromGetter = Assert.Throws<FormatException>(() => reader.GetInt32(0));

        Assert.DoesNotContain(stored, fromGetValue.Message, StringComparison.Ordinal);
        Assert.Equal(fromGetValue.Message, fromGetter.Message);
    }

    [Fact]
    public void Int64_WideDigits_Parse()
    {
        using var reader = Converted('C', 11, "12345678901", DbfFieldConversion.Int64());

        Assert.Equal(12345678901L, reader.GetValue(0));
    }

    [Fact]
    public void Decimal_NumberWithPoint_Parses()
    {
        using var reader = Converted('C', 8, "  12.50", DbfFieldConversion.Decimal());

        Assert.Equal(typeof(decimal), reader.GetFieldType(0));
        Assert.Equal(12.50m, reader.GetDecimal(0));
    }

    [Fact]
    public void ConvertedField_KeepsRawTextAndRejectsGetString()
    {
        using var reader = Converted('C', 4, "0034", DbfFieldConversion.Int32());

        Assert.Equal("0034"u8.ToArray(), reader.GetRawBytes(0).ToArray());
        Assert.Throws<InvalidCastException>(() => reader.GetString(0));
    }

    [Fact]
    public void ConvertedField_IsReflectedInFieldsAndSchemaButNotInHeader()
    {
        using var reader = Converted('C', 8, "31012024", DbfFieldConversion.Date("ddMMyyyy"));

        Assert.NotNull(reader.Fields[0].Conversion);
        Assert.Equal('C', reader.Fields[0].TypeCode);
        Assert.Null(reader.Header.Fields[0].Conversion);
        Assert.Equal(typeof(string), reader.Header.Fields[0].FieldType);
        var schema = reader.GetSchemaTable().Rows[0];
        Assert.Equal(typeof(DateTime), schema[SchemaTableColumn.DataType]);
        Assert.Equal(true, schema[SchemaTableColumn.AllowDBNull]);
        Assert.Equal(typeof(DateTime), reader.GetColumnSchema()[0].DataType);
    }

    [Fact]
    public void Conversions_NameIsCaseInsensitive()
    {
        var dbf = new DbfBuilder().Field("IDADE", 'C', 3).Record("042").Build();
        var options = new DbfReadOptions();
        options.Conversions["idade"] = DbfFieldConversion.Int32();
        using var reader = DbfReader.Open(new MemoryStream(dbf), options);
        Assert.True(reader.Read());

        Assert.Equal(42, reader.GetInt32("IDADE"));
    }

    [Fact]
    public void Conversions_WorkWithProjectionAndIgnoreFieldsLeftOut()
    {
        var dbf = new DbfBuilder().Field("A", 'C', 3).Field("B", 'C', 8).Record("001", "31012024").Build();
        var options = new DbfReadOptions { Columns = ["B"] };
        options.Conversions["A"] = DbfFieldConversion.Int32();
        options.Conversions["B"] = DbfFieldConversion.Date("ddMMyyyy");
        using var reader = DbfReader.Open(new MemoryStream(dbf), options);
        Assert.True(reader.Read());

        Assert.Equal(1, reader.FieldCount);
        Assert.Equal(new DateTime(2024, 1, 31), reader.GetDateTime(0));
    }

    [Fact]
    public void Conversions_UnknownField_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => OpenWith('C', 3, "NOPE", DbfFieldConversion.Int32()));
    }

    [Fact]
    public void Conversions_NonCharacterField_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => OpenWith('N', 3, "F", DbfFieldConversion.Int32()));
    }

    [Fact]
    public void Conversions_DateOnNarrowField_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => OpenWith('C', 6, "F", DbfFieldConversion.Date("yyyyMMdd")));
    }

    [Theory]
    [InlineData("yyyyMM")]
    [InlineData("dd/MM/yyyy")]
    [InlineData("YYYYMMDD")]
    public void Date_UnsupportedFormat_ThrowsArgumentException(string format)
    {
        Assert.Throws<ArgumentException>(() => DbfFieldConversion.Date(format));
    }

    [Fact]
    public void NativeIntegerField_WithFraction_ThrowsFormatException()
    {
        var dbf = new DbfBuilder().Field("QTY", 'N', 5).Record("  1.5").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Throws<FormatException>(() => reader.GetValue(0));
        Assert.Throws<FormatException>(() => reader.GetInt32(0));
    }

    private static DbfReader Converted(char type, int length, string stored, DbfFieldConversion conversion)
    {
        var reader = OpenWith(type, length, "F", conversion, stored);
        Assert.True(reader.Read());
        return reader;
    }

    private static DbfReader OpenWith(char type, int length, string convertedName, DbfFieldConversion conversion, string stored = "")
    {
        var dbf = new DbfBuilder().Field("F", type, length).Record(stored).Build();
        var options = new DbfReadOptions();
        options.Conversions[convertedName] = conversion;
        return DbfReader.Open(new MemoryStream(dbf), options);
    }
}
