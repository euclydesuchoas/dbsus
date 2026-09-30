using System.Data;
using System.Data.Common;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

public class DbfReaderAccessTests
{
    private static readonly byte[] Table = new DbfBuilder()
        .Field("CODE", 'C', 6)
        .Field("QTY", 'N', 5)
        .Field("PRICE", 'N', 8, 2)
        .Field("WHEN", 'D', 8)
        .Record("000001", "    2", "   10.00", "20240101")
        .DeletedRecord("000002", "    9", "   99.00", "20240102")
        .Record("000003", "     ", "    0.50", "        ")
        .Build();

    [Fact]
    public void GetOrdinal_IgnoresCase()
    {
        using var reader = Open();

        Assert.Equal(1, reader.GetOrdinal("qty"));
        Assert.Equal(1, reader.GetOrdinal("QTY"));
    }

    [Fact]
    public void GetOrdinal_UnknownName_ThrowsIndexOutOfRangeException()
    {
        using var reader = Open();

        Assert.Throws<IndexOutOfRangeException>(() => reader.GetOrdinal("MISSING"));
        Assert.Throws<IndexOutOfRangeException>(() => reader["MISSING"]);
    }

    [Fact]
    public void TryGetOrdinal_ReportsWithoutThrowing()
    {
        using var reader = Open();

        Assert.True(reader.TryGetOrdinal("price", out var ordinal));
        Assert.Equal(2, ordinal);
        Assert.False(reader.TryGetOrdinal("MISSING", out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void Getters_OrdinalOutOfRange_ThrowIndexOutOfRangeException(int ordinal)
    {
        using var reader = Open();
        Assert.True(reader.Read());

        Assert.Throws<IndexOutOfRangeException>(() => reader.GetValue(ordinal));
        Assert.Throws<IndexOutOfRangeException>(() => reader.GetName(ordinal));
    }

    [Fact]
    public void ByNameGetters_MatchOrdinalGetters()
    {
        using var reader = Open();
        Assert.True(reader.Read());

        Assert.Equal("000001", reader.GetString("CODE"));
        Assert.Equal(2, reader.GetInt32("qty"));
        Assert.Equal(10.00m, reader.GetDecimal("Price"));
        Assert.Equal(new DateTime(2024, 1, 1), reader.GetDateTime("WHEN"));
        Assert.Equal(2, reader["QTY"]);
        Assert.Equal(2, reader[1]);
    }

    [Fact]
    public void DuplicateFieldNames_FirstOneWins()
    {
        var dbf = new DbfBuilder().Field("A", 'C', 1).Field("A", 'C', 1).Record("1", "2").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));
        Assert.True(reader.Read());

        Assert.Equal(0, reader.GetOrdinal("A"));
        Assert.Equal("2", reader.GetString(1));
    }

    [Fact]
    public void ValueAccess_WithoutCurrentRecord_ThrowsInvalidOperationException()
    {
        using var reader = Open();

        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
        while (reader.Read())
        {
        }

        Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
    }

    [Fact]
    public void Read_SkipsDeletedRecordsByDefault()
    {
        using var reader = Open();

        Assert.True(reader.Read());
        Assert.Equal(1, reader.RecordNumber);
        Assert.True(reader.Read());
        Assert.Equal("000003", reader.GetString(0));
        Assert.Equal(3, reader.RecordNumber);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Read_IncludeDeletedRecords_ReturnsAndFlagsThem()
    {
        using var reader = Open(new DbfReadOptions { IncludeDeletedRecords = true });

        Assert.True(reader.Read());
        Assert.False(reader.IsDeleted);
        Assert.True(reader.Read());
        Assert.True(reader.IsDeleted);
        Assert.Equal("000002", reader.GetString(0));
        Assert.True(reader.Read());
        Assert.False(reader.Read());
    }

    [Fact]
    public void Read_StopsAtDeclaredRecordCount()
    {
        var dbf = new DbfBuilder { DeclaredRecordCount = 1, EndOfFileMarker = false }.Field("A", 'C', 1).Record("1").Record("2").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.True(reader.Read());
        Assert.False(reader.Read());
    }

    [Fact]
    public void Read_DataEndsBeforeDeclaredRecords_ThrowsInvalidDataException()
    {
        var dbf = new DbfBuilder { DeclaredRecordCount = 3, EndOfFileMarker = false }.Field("A", 'C', 4).Record("1").Record("2").Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf, 0, dbf.Length - 2));

        Assert.True(reader.Read());
        var exception = Assert.Throws<InvalidDataException>(() => reader.Read());
        Assert.Contains("record 2 of 3", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_NoRecords_ReturnsFalse()
    {
        var dbf = new DbfBuilder().Field("A", 'C', 1).Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf));

        Assert.False(reader.HasRows);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Columns_ExposeProjectionInRequestedOrder()
    {
        using var reader = Open(new DbfReadOptions { Columns = ["when", "CODE"] });
        Assert.True(reader.Read());

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal(["WHEN", "CODE"], reader.Fields.Select(field => field.Name));
        Assert.Equal(new DateTime(2024, 1, 1), reader.GetDateTime(0));
        Assert.Equal("000001", reader.GetString(1));
        Assert.Equal(1, reader.GetOrdinal("CODE"));
        Assert.False(reader.TryGetOrdinal("QTY", out _));
        Assert.Equal(4, reader.Header.Fields.Count);
    }

    [Fact]
    public void Columns_UnknownName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Open(new DbfReadOptions { Columns = ["CODE", "NOPE"] }));
    }

    [Fact]
    public void Columns_RepeatedName_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => Open(new DbfReadOptions { Columns = ["CODE", "code"] }));
    }

    [Fact]
    public void GetSchemaTable_DescribesExposedFields()
    {
        using var reader = Open();

        var schema = reader.GetSchemaTable();

        Assert.Equal(4, schema.Rows.Count);
        var price = schema.Rows[2];
        Assert.Equal("PRICE", price[SchemaTableColumn.ColumnName]);
        Assert.Equal(2, price[SchemaTableColumn.ColumnOrdinal]);
        Assert.Equal(8, price[SchemaTableColumn.ColumnSize]);
        Assert.Equal((short)8, price[SchemaTableColumn.NumericPrecision]);
        Assert.Equal((short)2, price[SchemaTableColumn.NumericScale]);
        Assert.Equal(typeof(decimal), price[SchemaTableColumn.DataType]);
        Assert.Equal(true, price[SchemaTableColumn.AllowDBNull]);
        Assert.Equal(false, schema.Rows[0][SchemaTableColumn.AllowDBNull]);
    }

    [Fact]
    public void GetColumnSchema_DescribesExposedFields()
    {
        using var reader = Open(new DbfReadOptions { BlankStringsAsNull = true });

        var columns = reader.GetColumnSchema();

        Assert.Equal(["CODE", "QTY", "PRICE", "WHEN"], columns.Select(column => column.ColumnName));
        Assert.Equal(typeof(int), columns[1].DataType);
        Assert.Equal(true, columns[0].AllowDBNull);
        Assert.Equal(2, columns[2].NumericScale);
    }

    [Fact]
    public void DataTableLoad_LoadsRowsWithTypesAndNulls()
    {
        using var reader = Open();
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };

        table.Load(reader);

        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(typeof(int), table.Columns["QTY"]!.DataType);
        Assert.Equal(2, table.Rows[0]["QTY"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["QTY"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["WHEN"]);
        Assert.Equal(0.50m, table.Rows[1]["PRICE"]);
    }

    [Fact]
    public void GetValues_FillsTheArray()
    {
        using var reader = Open();
        Assert.True(reader.Read());
        var values = new object[4];

        Assert.Equal(4, reader.GetValues(values));
        Assert.Equal(new object[] { "000001", 2, 10.00m, new DateTime(2024, 1, 1) }, values);
    }

    private static DbfReader Open(DbfReadOptions? options = null) => DbfReader.Open(new MemoryStream(Table), options);
}
