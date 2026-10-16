#if NET
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

// GC.GetAllocatedBytesForCurrentThread is not available on .NET Framework, so this guard runs on
// the net10.0 target only. Each measurement goes through AllocationMeasurement, which repeats it.
public class DbfReaderAllocationTests
{
    [Fact]
    public void ReadAndTypedGetters_DoNotAllocatePerRecord()
    {
        var builder = new DbfBuilder()
            .Field("QTY", 'N', 11)
            .Field("SMALL", 'N', 5)
            .Field("VAL", 'N', 20, 2)
            .Field("AGE", 'C', 3)
            .Field("WHEN", 'D', 8)
            .Field("BIRTH", 'C', 8)
            .Field("FLAG", 'L', 1);
        for (var i = 0; i < 1000; i++)
        {
            builder.Record("         42", "  123", "            12345.67", "034", "20240101", "31012024", "T");
        }

        var options = new DbfReadOptions();
        options.Conversions["AGE"] = DbfFieldConversion.Int32();
        options.Conversions["BIRTH"] = DbfFieldConversion.Date("ddMMyyyy");
        var file = builder.Build();

        AllocationMeasurement.AssertNoAllocation(() =>
        {
            using var reader = DbfReader.Open(new MemoryStream(file), options);
            Assert.True(reader.Read());
            ReadTyped(reader); // warm up

            var before = GC.GetAllocatedBytesForCurrentThread();
            var records = 0;
            while (reader.Read())
            {
                ReadTyped(reader);
                records++;
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(999, records);
            return allocated;
        });
    }

    [Theory]
    [InlineData(0x02)] // code page 850
    [InlineData(0x03)] // code page 1252
    [InlineData(0x57)] // ISO-8859-1
    public void GetChars_SingleByteEncoding_DoesNotAllocatePerRecord(byte languageDriver)
    {
        var builder = new DbfBuilder { LanguageDriverId = languageDriver }.Field("NAME", 'C', 12);
        for (var i = 0; i < 1000; i++)
        {
            builder.Record("0301010019  ");
        }

        var file = builder.Build();
        var buffer = new char[16];

        AllocationMeasurement.AssertNoAllocation(() =>
        {
            using var reader = DbfReader.Open(new MemoryStream(file));
            Assert.True(reader.Read());
            _ = reader.GetChars(0, 0, buffer, 0, buffer.Length); // warm up
            _ = reader.GetChars(0, 4, buffer, 0, 2);
            _ = reader.GetChars(0, 0, null, 0, 0);

            var before = GC.GetAllocatedBytesForCurrentThread();
            long chars = 0;
            while (reader.Read())
            {
                chars += reader.GetChars(0, 0, buffer, 0, buffer.Length);
                chars += reader.GetChars(0, 4, buffer, 0, 2);
                chars += reader.GetChars(0, 0, null, 0, 0);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(999 * (10 + 2 + 10), chars);
            return allocated;
        });
    }

    private static void ReadTyped(DbfReader reader)
    {
        _ = reader.GetInt64(0);
        _ = reader.GetInt32(1);
        _ = reader.GetDecimal(2);
        _ = reader.IsDBNull(3);
        _ = reader.GetInt32(3);
        _ = reader.GetDateTime(4);
        _ = reader.GetDateTime(5);
        _ = reader.GetBoolean(6);
        _ = reader.GetDouble(2);
        _ = reader.GetFieldValue<int?>(3);
        _ = reader.GetRawBytes(3);
    }
}
#endif
