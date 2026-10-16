using System.Data;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

// DbfReadOptions.UnconvertibleValues: what the reader does with the content of an N, F or D field
// that is not a number or a date. Every stored content here is made up.
public class DbfReaderUnconvertibleValuesTests
{
    private const string Hint = "; to read such a value as null, see DbfReadOptions.UnconvertibleValues.";
    private const string NullMessage = "Field F is null in the current record; check IsDBNull first.";

    // Field shapes: a narrow N (read as Int32), a long N (Int64), an N with decimals, an F and a D.
    // Contents that do not convert: the field filled with asterisks, asterisks with spaces at the
    // ends, asterisks among digits, a letter, a decimal comma and slashes.
    private static readonly (char Type, int Length, int Decimals, string Stored)[] AsteriskRows =
    [
        ('N', 8, 0, "********"), ('N', 8, 0, "  ****  "), ('N', 8, 0, "       *"),
        ('N', 12, 0, "************"), ('N', 12, 0, " ***        "),
        ('N', 8, 2, "********"), ('N', 8, 2, "   **   "),
        ('F', 8, 2, "********"), ('F', 8, 2, "***     "),
        ('D', 8, 0, "********"), ('D', 8, 0, "  ***   "),
    ];

    private static readonly (char Type, int Length, int Decimals, string Stored)[] OtherRows =
    [
        ('N', 8, 0, "12**34  "), ('N', 8, 0, "   A    "), ('N', 8, 0, "    1,50"), ('N', 8, 0, "   *1   "), ('N', 8, 0, "** **   "),
        ('N', 8, 0, "    1.50"),      // a fraction in a field with no decimals
        ('N', 12, 0, "99*9        "), ('N', 12, 0, "     X      "), ('N', 12, 0, "        1.50"),
        ('N', 8, 2, "12**34  "), ('N', 8, 2, "       A"), ('N', 8, 2, "    1,50"),
        ('F', 8, 2, "1*      "), ('F', 8, 2, "    A   "), ('F', 8, 2, "   1,5  "),
        ('D', 8, 0, "31/01/24"), ('D', 8, 0, "2024**01"), ('D', 8, 0, "20241340"), ('D', 8, 0, "   A    "),
    ];

    public static TheoryData<char, int, int, string> Asterisks => ToData(AsteriskRows);

    public static TheoryData<char, int, int, string> OtherContent => ToData(OtherRows);

    public static TheoryData<char, int, int, string> EveryContent => ToData(AsteriskRows.Concat(OtherRows));

    [Fact]
    public void Default_IsThrow()
    {
        Assert.Equal(DbfUnconvertibleValueHandling.Throw, new DbfReadOptions().UnconvertibleValues);
        Assert.Equal(0, (int)DbfUnconvertibleValueHandling.Throw);
        Assert.Equal(1, (int)DbfUnconvertibleValueHandling.AsterisksAsNull);
        Assert.Equal(2, (int)DbfUnconvertibleValueHandling.Null);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void Open_ValueOutsideTheEnumeration_Throws(int value)
    {
        var dbf = new DbfBuilder().Field("F", 'N', 3).Record("  1").Build();
        var options = new DbfReadOptions { UnconvertibleValues = (DbfUnconvertibleValueHandling)value };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DbfReader.Open(new MemoryStream(dbf), options));

        Assert.Contains("UnconvertibleValues", exception.Message, StringComparison.Ordinal);
    }

    // The default, written out or left out: exactly what the reader did before the option existed.
    [Theory]
    [MemberData(nameof(EveryContent))]
    public void Throw_ContentThatDoesNotConvert_ThrowsFormatException_AndIsNotNull(char type, int length, int decimals, string stored)
    {
        foreach (var options in new[] { null, new DbfReadOptions(), new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.Throw } })
        {
            using var reader = Open(type, length, decimals, options, stored);

            Assert.False(reader.IsDBNull(0));
            var exception = Assert.Throws<FormatException>(() => reader.GetValue(0));
            Assert.Throws<FormatException>(() => reader[0]);
            Assert.Throws<FormatException>(() => reader["F"]);
            Assert.Throws<FormatException>(() => reader.GetValues(new object[1]));
            Assert.Throws<FormatException>(() => reader.GetProviderSpecificValue(0));
            Assert.Throws<FormatException>(() => Typed(reader, type, length, decimals));
            Assert.Throws<FormatException>(() => Nullable(reader, type, length, decimals));
            Assert.Throws<FormatException>(() => NotNullable(reader, type, length, decimals));
            Assert.Equal(stored, System.Text.Encoding.ASCII.GetString(reader.GetRawBytes(0).ToArray()));

            // The message names the field, its type and the record, points at the option, and
            // never has the content.
            Assert.StartsWith($"Field F ({type}) of record 1 does not hold ", exception.Message, StringComparison.Ordinal);
            Assert.EndsWith(Hint, exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(stored.Trim(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData('N', 8, 0, "   A    ", "Field F (N) of record 1 does not hold a number" + Hint)]
    [InlineData('N', 8, 0, "    1.50", "Field F (N) of record 1 does not hold a whole number that fits in Int32" + Hint)]
    [InlineData('N', 12, 0, "        1.50", "Field F (N) of record 1 does not hold a whole number that fits in Int64" + Hint)]
    [InlineData('N', 8, 2, "********", "Field F (N) of record 1 does not hold a number" + Hint)]
    [InlineData('F', 8, 2, "    1,50", "Field F (F) of record 1 does not hold a number" + Hint)]
    [InlineData('D', 8, 0, "31/01/24", "Field F (D) of record 1 does not hold a yyyyMMdd date" + Hint)]
    public void Throw_Message_PointsAtTheOption(char type, int length, int decimals, string stored, string expected)
    {
        using var reader = Open(type, length, decimals, null, stored);

        Assert.Equal(expected, Assert.Throws<FormatException>(() => reader.GetValue(0)).Message);
    }

    // The option is not for logical fields or for converted character fields: their message does
    // not point at it, and they throw in every mode.
    [Theory]
    [InlineData(DbfUnconvertibleValueHandling.Throw)]
    [InlineData(DbfUnconvertibleValueHandling.AsterisksAsNull)]
    [InlineData(DbfUnconvertibleValueHandling.Null)]
    public void EveryMode_LogicalAndConvertedCharacterFields_ThrowAsBefore(DbfUnconvertibleValueHandling mode)
    {
        var dbf = new DbfBuilder()
            .Field("FLAG", 'L', 1).Field("AGE", 'C', 3).Field("BORN", 'C', 8).Field("STARS", 'C', 3)
            .Record("*", "***", "********", "***")
            .Record("X", "1A ", "31/01/24", "***")
            .Build();
        var options = new DbfReadOptions { UnconvertibleValues = mode };
        options.Conversions["AGE"] = DbfFieldConversion.Int32();
        options.Conversions["BORN"] = DbfFieldConversion.Date("ddMMyyyy");
        using var reader = DbfReader.Open(new MemoryStream(dbf), options);

        while (reader.Read())
        {
            var flag = Assert.Throws<FormatException>(() => reader.GetValue(0));
            var age = Assert.Throws<FormatException>(() => reader.GetValue(1));
            var born = Assert.Throws<FormatException>(() => reader.GetValue(2));

            Assert.Equal($"Field FLAG (L) of record {reader.RecordNumber} does not hold a logical value.", flag.Message);
            Assert.Equal($"Field AGE (C) of record {reader.RecordNumber} does not hold a number.", age.Message);
            Assert.Equal($"Field BORN (C) of record {reader.RecordNumber} does not hold a ddMMyyyy date.", born.Message);
            Assert.False(reader.IsDBNull(0));
            Assert.False(reader.IsDBNull(1));
            Assert.False(reader.IsDBNull(2));
            Assert.Throws<FormatException>(() => reader.GetBoolean(0));
            Assert.Throws<FormatException>(() => reader.GetInt32(1));
            Assert.Throws<FormatException>(() => reader.GetDateTime(2));

            // A character field that is not converted is text, whatever it holds.
            Assert.Equal("***", reader.GetValue(3));
        }
    }

    // Null: whatever does not convert is null, exactly like a blank field of the same record layout.
    [Theory]
    [MemberData(nameof(EveryContent))]
    public void Null_ContentThatDoesNotConvert_IsReadLikeABlankField(char type, int length, int decimals, string stored)
    {
        AssertReadLikeABlankField(type, length, decimals, stored, DbfUnconvertibleValueHandling.Null);
    }

    // AsterisksAsNull: a field that, without the spaces at its ends, is asterisks and nothing else.
    [Theory]
    [MemberData(nameof(Asterisks))]
    public void AsterisksAsNull_FieldOfAsterisks_IsReadLikeABlankField(char type, int length, int decimals, string stored)
    {
        AssertReadLikeABlankField(type, length, decimals, stored, DbfUnconvertibleValueHandling.AsterisksAsNull);
    }

    // AsterisksAsNull: any other content that does not convert still throws, and is not null.
    [Theory]
    [MemberData(nameof(OtherContent))]
    public void AsterisksAsNull_OtherContent_ThrowsAsInThrow(char type, int length, int decimals, string stored)
    {
        using var reader = Open(type, length, decimals, new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.AsterisksAsNull }, stored);

        Assert.False(reader.IsDBNull(0));
        var exception = Assert.Throws<FormatException>(() => reader.GetValue(0));
        Assert.Throws<FormatException>(() => reader[0]);
        Assert.Throws<FormatException>(() => reader.GetValues(new object[1]));
        Assert.Throws<FormatException>(() => reader.GetProviderSpecificValue(0));
        Assert.Throws<FormatException>(() => Typed(reader, type, length, decimals));
        Assert.Throws<FormatException>(() => Nullable(reader, type, length, decimals));
        Assert.Throws<FormatException>(() => NotNullable(reader, type, length, decimals));
        Assert.EndsWith(Hint, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(stored.Trim(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(stored, System.Text.Encoding.ASCII.GetString(reader.GetRawBytes(0).ToArray()));
    }

    // In every mode: a blank field and a date of zeros are null, and what converts is the value.
    [Theory]
    [InlineData(DbfUnconvertibleValueHandling.Throw)]
    [InlineData(DbfUnconvertibleValueHandling.AsterisksAsNull)]
    [InlineData(DbfUnconvertibleValueHandling.Null)]
    public void EveryMode_BlankZerosAndGoodValues_AreReadAsBefore(DbfUnconvertibleValueHandling mode)
    {
        var dbf = new DbfBuilder()
            .Field("INT", 'N', 8).Field("LONG", 'N', 12).Field("DEC", 'N', 8, 2).Field("FLOAT", 'F', 8, 2).Field("DATE", 'D', 8).Field("TEXT", 'C', 4)
            .Record("        ", "            ", "        ", "        ", "        ", "    ")
            .Record("      42", "  1234567890", "   12.50", "    0.25", "20240131", "**  ")
            .Record("      -7", "          -1", "   -1.00", "1.25    ", "00000000", "a,b ")
            .Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { UnconvertibleValues = mode });

        Assert.True(reader.Read());
        for (var ordinal = 0; ordinal < 5; ordinal++)
        {
            Assert.True(reader.IsDBNull(ordinal));
            Assert.Equal(DBNull.Value, reader.GetValue(ordinal));
        }

        Assert.Equal(string.Empty, reader.GetValue(5));
        Assert.True(reader.Read());
        Assert.Equal([42, 1234567890L, 12.50m, 0.25m, new DateTime(2024, 1, 31), "**"], Values(reader));
        Assert.Equal(42, reader.GetInt32(0));
        Assert.Equal(1234567890L, reader.GetInt64(1));
        Assert.Equal(12.50m, reader.GetDecimal(2));
        Assert.Equal(0.25, reader.GetDouble(3));
        Assert.Equal(new DateTime(2024, 1, 31), reader.GetDateTime(4));
        for (var ordinal = 0; ordinal < 6; ordinal++)
        {
            Assert.False(reader.IsDBNull(ordinal));
        }

        Assert.True(reader.Read());
        Assert.Equal([-7, -1L, -1.00m, 1.25m, DBNull.Value, "a,b"], Values(reader));
        Assert.True(reader.IsDBNull(4));
        Assert.False(reader.Read());
    }

    // A file like the ones that led to the option: fields of asterisks in most records, and a
    // record with a date with slashes and a letter in a number.
    [Fact]
    public void WholeTable_EachMode()
    {
        var dbf = WholeTable();

        // Throw: the first value that does not convert stops a loop that reads every value.
        using (var reader = DbfReader.Open(new MemoryStream(dbf)))
        {
            Assert.True(reader.Read());
            Assert.Equal(["0001", 3, 12.5m, new DateTime(2024, 1, 31)], Values(reader));
            Assert.True(reader.Read());
            Assert.Throws<FormatException>(() => Values(reader));
        }

        // AsterisksAsNull: the records of asterisks are read; the one with other content throws,
        // and the reader goes on after it.
        using (var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.AsterisksAsNull }))
        {
            Assert.True(reader.Read());
            Assert.Equal(["0001", 3, 12.5m, new DateTime(2024, 1, 31)], Values(reader));
            Assert.True(reader.Read());
            Assert.Equal(["0002", DBNull.Value, DBNull.Value, DBNull.Value], Values(reader));
            Assert.True(reader.Read());
            Assert.Equal(["0003", 4, DBNull.Value, new DateTime(2024, 2, 1)], Values(reader));
            Assert.True(reader.Read());
            var exception = Assert.Throws<FormatException>(() => Values(reader));
            Assert.Equal("Field QTY (N) of record 4 does not hold a number" + Hint, exception.Message);
            Assert.Equal("0004", reader.GetString(0));
            Assert.True(reader.IsDBNull(2));
            Assert.Throws<FormatException>(() => reader.GetDateTime(3));
            Assert.True(reader.Read());
            Assert.Equal(["0005", 5, 1m, DBNull.Value], Values(reader));
            Assert.False(reader.Read());
        }

        // Null: every record is read.
        using (var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.Null }))
        {
            var rows = new List<object[]>();
            while (reader.Read())
            {
                rows.Add(Values(reader));
            }

            Assert.Equal(5, rows.Count);
            Assert.Equal(["0004", DBNull.Value, DBNull.Value, DBNull.Value], rows[3]);
            Assert.Equal(["0002", DBNull.Value, DBNull.Value, DBNull.Value], rows[1]);
        }
    }

    [Fact]
    public void DataTableLoad_EachMode()
    {
        var dbf = WholeTable();

        Assert.Throws<FormatException>(() => Load(dbf, DbfUnconvertibleValueHandling.Throw));
        Assert.Throws<FormatException>(() => Load(dbf, DbfUnconvertibleValueHandling.AsterisksAsNull));

        using var table = Load(dbf, DbfUnconvertibleValueHandling.Null);
        Assert.Equal(5, table.Rows.Count);
        Assert.Equal(typeof(int), table.Columns["QTY"]!.DataType);
        Assert.Equal(typeof(DateTime), table.Columns["WHEN"]!.DataType);
        Assert.Equal(DBNull.Value, table.Rows[1]["QTY"]);
        Assert.Equal(DBNull.Value, table.Rows[1]["VAL"]);
        Assert.Equal(DBNull.Value, table.Rows[3]["WHEN"]);
        Assert.Equal(4, table.Rows[2]["QTY"]);

        // Only asterisks in the table: AsterisksAsNull loads it too.
        var asterisks = new DbfBuilder().Field("QTY", 'N', 4).Field("WHEN", 'D', 8).Record("****", "********").Record("   1", "20240131").Build();
        using var loaded = Load(asterisks, DbfUnconvertibleValueHandling.AsterisksAsNull);
        Assert.Equal(2, loaded.Rows.Count);
        Assert.Equal(DBNull.Value, loaded.Rows[0]["QTY"]);
        Assert.Equal(DBNull.Value, loaded.Rows[0]["WHEN"]);
        Assert.Equal(1, loaded.Rows[1]["QTY"]);
    }

    // A projection that leaves the fields out reads the table in the default mode; one that keeps
    // them is under the option like any other reader.
    [Fact]
    public void Columns_LeavingTheFieldsOut_ReadsInThrow_AndTheOptionAppliesToTheOnesKept()
    {
        var dbf = WholeTable();

        using (var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { Columns = ["ID"] }))
        {
            var ids = new List<string>();
            while (reader.Read())
            {
                ids.Add((string)reader.GetValue(0));
            }

            Assert.Equal(["0001", "0002", "0003", "0004", "0005"], ids);
        }

        using (var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { Columns = ["WHEN", "ID"] }))
        {
            Assert.True(reader.Read());
            Assert.True(reader.Read());
            Assert.Throws<FormatException>(() => reader.GetValue(0));
        }

        using (var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { Columns = ["WHEN", "ID"], UnconvertibleValues = DbfUnconvertibleValueHandling.Null }))
        {
            var dates = new List<object>();
            while (reader.Read())
            {
                dates.Add(reader.GetValue(0));
            }

            Assert.Equal([new DateTime(2024, 1, 31), DBNull.Value, new DateTime(2024, 2, 1), DBNull.Value, DBNull.Value], dates);
        }
    }

    // A table in parts: the option is of the reader, so it holds in every part.
    [Fact]
    public void Parts_TheOptionHoldsInEveryPart()
    {
        var first = new DbfBuilder().Field("QTY", 'N', 3).Record("  1").Record("***").Build();
        var second = new DbfBuilder().Field("QTY", 'N', 3).Record("***").Record("x9x").Build();

        using (var reader = DbfReader.Open([new MemoryStream(first), new MemoryStream(second)], new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.AsterisksAsNull }))
        {
            Assert.True(reader.Read());
            Assert.Equal(1, reader.GetValue(0));
            Assert.True(reader.Read());
            Assert.Equal(DBNull.Value, reader.GetValue(0));
            Assert.True(reader.Read());
            Assert.Equal(DBNull.Value, reader.GetValue(0));
            Assert.True(reader.Read());
            Assert.Equal("Field QTY (N) of record 4 (part 2) does not hold a number" + Hint, Assert.Throws<FormatException>(() => reader.GetValue(0)).Message);
        }

        using (var reader = DbfReader.Open([new MemoryStream(first), new MemoryStream(second)], new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.Null }))
        {
            var values = new List<object>();
            while (reader.Read())
            {
                values.Add(reader.GetValue(0));
            }

            Assert.Equal([1, DBNull.Value, DBNull.Value, DBNull.Value], values);
        }
    }

    // The schema does not depend on the option: these fields allowed null already.
    [Theory]
    [InlineData(DbfUnconvertibleValueHandling.Throw)]
    [InlineData(DbfUnconvertibleValueHandling.Null)]
    public void Schema_IsTheSameInEveryMode(DbfUnconvertibleValueHandling mode)
    {
        using var reader = DbfReader.Open(new MemoryStream(WholeTable()), new DbfReadOptions { UnconvertibleValues = mode });

        Assert.Equal([typeof(string), typeof(int), typeof(decimal), typeof(DateTime)], Enumerable.Range(0, 4).Select(reader.GetFieldType));
        using var schema = reader.GetSchemaTable()!;
        Assert.Equal([false, true, true, true], schema.Rows.Cast<DataRow>().Select(row => (bool)row["AllowDBNull"]));
    }

#if NET
    // GC.GetAllocatedBytesForCurrentThread is not available on .NET Framework. The path of the
    // default mode is guarded by DbfReaderAllocationTests; this one guards the two modes that read
    // null, on values that convert and on values that do not.
    [Theory]
    [InlineData(DbfUnconvertibleValueHandling.AsterisksAsNull)]
    [InlineData(DbfUnconvertibleValueHandling.Null)]
    public void NullModes_TypedGettersAndIsDBNull_DoNotAllocatePerRecord(DbfUnconvertibleValueHandling mode)
    {
        var builder = new DbfBuilder().Field("QTY", 'N', 8).Field("VAL", 'N', 12, 2).Field("WHEN", 'D', 8).Field("STARS", 'N', 8).Field("GONE", 'D', 8);
        for (var i = 0; i < 1000; i++)
        {
            builder.Record("      42", "    12345.67", "20240101", "********", "********");
        }

        var file = builder.Build();
        var options = new DbfReadOptions { UnconvertibleValues = mode };

        AllocationMeasurement.AssertNoAllocation(() =>
        {
            using var reader = DbfReader.Open(new MemoryStream(file), options);
            Assert.True(reader.Read());
            long nulls = ReadGuarded(reader); // warm up

            var before = GC.GetAllocatedBytesForCurrentThread();
            var records = 0;
            while (reader.Read())
            {
                nulls += ReadGuarded(reader);
                records++;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(999, records);
            Assert.Equal(2000, nulls);
            return allocated;
        });
    }

    private static int ReadGuarded(DbfReader reader)
    {
        var nulls = 0;
        _ = reader.IsDBNull(0) ? nulls++ : reader.GetInt32(0);
        _ = reader.IsDBNull(1) ? nulls++ : (int)reader.GetDecimal(1);
        _ = reader.IsDBNull(2) ? nulls++ : reader.GetDateTime(2).Year;
        _ = reader.IsDBNull(3) ? nulls++ : reader.GetInt32(3);
        _ = reader.IsDBNull(4) ? nulls++ : reader.GetDateTime(4).Year;
        _ = reader.GetRawBytes(3);
        return nulls;
    }
#endif

    // One record that is blank and one with the content: in a mode that reads the content as null,
    // every member gives for the second what it gives for the first.
    private static void AssertReadLikeABlankField(char type, int length, int decimals, string stored, DbfUnconvertibleValueHandling mode)
    {
        var dbf = new DbfBuilder().Field("F", type, length, decimals).Record(new string(' ', length)).Record(stored).Record(stored).Build();
        using var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { UnconvertibleValues = mode });
        Assert.True(reader.Read());
        var blank = Outcomes(reader, type, length, decimals);
        Assert.True(reader.Read());
        var content = Outcomes(reader, type, length, decimals);

        Assert.Equal(blank, content);

        // And what that is: null everywhere, and the exception of a null value in the typed getters.
        Assert.True(reader.IsDBNull(0));
        Assert.True(reader.IsDBNull("F"));
        Assert.Equal(DBNull.Value, reader.GetValue(0));
        Assert.Equal(DBNull.Value, reader[0]);
        Assert.Equal(DBNull.Value, reader["F"]);
        var values = new object[1];
        Assert.Equal(1, reader.GetValues(values));
        Assert.Equal(DBNull.Value, values[0]);
        Assert.Equal(DBNull.Value, reader.GetProviderSpecificValue(0));
        Assert.Null(Nullable(reader, type, length, decimals));
        Assert.Equal(NullMessage, Assert.Throws<InvalidCastException>(() => Typed(reader, type, length, decimals)).Message);
        Assert.Equal(NullMessage, Assert.Throws<InvalidCastException>(() => NotNullable(reader, type, length, decimals)).Message);
        Assert.Equal(stored, System.Text.Encoding.ASCII.GetString(reader.GetRawBytes(0).ToArray()));

        // The reader goes on.
        Assert.True(reader.Read());
        Assert.True(reader.IsDBNull(0));
        Assert.False(reader.Read());
    }

    // What each member does with the current record: its value, or the exception and its message.
    private static List<string> Outcomes(DbfReader reader, char type, int length, int decimals)
    {
        var outcomes = new List<string>
        {
            Outcome(() => reader.IsDBNull(0)),
            Outcome(() => reader.GetValue(0)),
            Outcome(() => reader[0]),
            Outcome(() => reader.GetProviderSpecificValue(0)),
            Outcome(() => reader.GetFieldValue<object>(0)),
            Outcome(() => Typed(reader, type, length, decimals)),
            Outcome(() => Nullable(reader, type, length, decimals)),
            Outcome(() => NotNullable(reader, type, length, decimals)),
        };
        if (type != 'D')
        {
            outcomes.Add(Outcome(() => reader.GetDecimal(0)));
            outcomes.Add(Outcome(() => reader.GetDouble(0)));
            outcomes.Add(Outcome(() => reader.GetFloat(0)));
            outcomes.Add(Outcome(() => reader.GetInt16(0)));
            outcomes.Add(Outcome(() => reader.GetInt32(0)));
            outcomes.Add(Outcome(() => reader.GetInt64(0)));
            outcomes.Add(Outcome(() => reader.GetFieldValue<int?>(0)));
            outcomes.Add(Outcome(() => reader.GetFieldValue<long?>(0)));
            outcomes.Add(Outcome(() => reader.GetFieldValue<double?>(0)));
            outcomes.Add(Outcome(() => reader.GetFieldValue<decimal>(0)));
        }

        return outcomes;
    }

    private static string Outcome(Func<object?> member)
    {
        try
        {
            var value = member();
            return value is null ? "null" : value == DBNull.Value ? "DBNull" : "value";
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            return exception.GetType().Name + ": " + exception.Message;
        }
    }

    // The typed getter of the field's own type (as GetFieldType gives it), and GetFieldValue of
    // that type, nullable and not.
    private static object Typed(DbfReader reader, char type, int length, int decimals) =>
        type == 'D' ? reader.GetDateTime(0) : IsInt32(type, length, decimals) ? reader.GetInt32(0) : IsInt64(type, length, decimals) ? reader.GetInt64(0) : reader.GetDecimal(0);

    private static object? Nullable(DbfReader reader, char type, int length, int decimals) =>
        type == 'D' ? reader.GetFieldValue<DateTime?>(0)
        : IsInt32(type, length, decimals) ? reader.GetFieldValue<int?>(0)
        : IsInt64(type, length, decimals) ? reader.GetFieldValue<long?>(0)
        : reader.GetFieldValue<decimal?>(0);

    private static object NotNullable(DbfReader reader, char type, int length, int decimals) =>
        type == 'D' ? reader.GetFieldValue<DateTime>(0)
        : IsInt32(type, length, decimals) ? reader.GetFieldValue<int>(0)
        : IsInt64(type, length, decimals) ? reader.GetFieldValue<long>(0)
        : reader.GetFieldValue<decimal>(0);

    private static bool IsInt32(char type, int length, int decimals) => type == 'N' && decimals == 0 && length <= 9;

    private static bool IsInt64(char type, int length, int decimals) => type == 'N' && decimals == 0 && length is > 9 and <= 18;

    private static TheoryData<char, int, int, string> ToData(IEnumerable<(char Type, int Length, int Decimals, string Stored)> rows)
    {
        var data = new TheoryData<char, int, int, string>();
        foreach (var (type, length, decimals, stored) in rows)
        {
            data.Add(type, length, decimals, stored);
        }

        return data;
    }

    private static object[] Values(DbfReader reader)
    {
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        return values;
    }

    private static DbfReader Open(char type, int length, int decimals, DbfReadOptions? options, string stored)
    {
        var dbf = new DbfBuilder().Field("F", type, length, decimals).Record(stored).Build();
        var reader = DbfReader.Open(new MemoryStream(dbf), options);
        Assert.True(reader.Read());
        return reader;
    }

    private static byte[] WholeTable() => new DbfBuilder()
        .Field("ID", 'C', 4).Field("QTY", 'N', 4).Field("VAL", 'N', 8, 2).Field("WHEN", 'D', 8)
        .Record("0001", "   3", "   12.50", "20240131")
        .Record("0002", "****", "********", "********")
        .Record("0003", "   4", "********", "20240201")
        .Record("0004", "   A", "********", "31/01/24")
        .Record("0005", "   5", "    1.00", "********")
        .Build();

    private static DataTable Load(byte[] dbf, DbfUnconvertibleValueHandling mode)
    {
        using var reader = DbfReader.Open(new MemoryStream(dbf), new DbfReadOptions { UnconvertibleValues = mode });
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };
        try
        {
            table.Load(reader);
            return table;
        }
        catch
        {
            table.Dispose();
            throw;
        }
    }
}
