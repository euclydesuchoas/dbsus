using System.Data;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbf;

/// <summary>
/// Reads the real DATASUS samples end to end through <see cref="DbSusFile"/>: every record, and
/// every value of every field for all but the largest files. The committed fixtures always run;
/// the other samples skip themselves when absent (see tools/fetch-samples).
/// </summary>
public class DbfSampleTests
{
    // Above this size only two fields per record are parsed, to keep the local run reasonable.
    private const long FullScanLimit = 20_000_000;

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ExpectedSamples.All)
        {
            data.Add(entry.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void OpenDbcReader_RealSample_ReadsEveryRecordAndParsesValues(string file)
    {
        var path = new[] { RepositoryPaths.Fixtures, RepositoryPaths.LocalSamples }
            .Select(directory => Path.Combine(directory, file))
            .FirstOrDefault(File.Exists);
        if (path is null)
        {
            Assert.Skip($"{file} is not in tests/data/fixtures or tests/data/local; run tools/fetch-samples.");
        }

        var fullScan = new FileInfo(path).Length <= FullScanLimit;
        using var reader = DbSusFile.OpenDbcReader(path);
        var values = new object[reader.FieldCount];
        var last = reader.FieldCount - 1;
        long records = 0;

        // Read() returning false also means the DBC checksum matched.
        while (reader.Read())
        {
            records++;
            if (fullScan)
            {
                reader.GetValues(values);
            }
            else
            {
                _ = reader.GetValue(0);
                _ = reader.GetValue(last);
            }
        }

        Assert.Equal(reader.Header.RecordCount, records);
        Assert.All(reader.Fields, field => Assert.True(field.IsSupported, $"{field} is not supported."));
    }

    [Fact]
    public void DataTableLoad_CommittedFixture_LoadsTypedColumns()
    {
        using var reader = DbSusFile.OpenDbcReader(Path.Combine(RepositoryPaths.Fixtures, "LTRR0610.dbc"));
        var table = new DataTable { Locale = System.Globalization.CultureInfo.InvariantCulture };

        table.Load(reader);

        Assert.Equal(102, table.Rows.Count);
        Assert.Equal(27, table.Columns.Count);
        Assert.Equal(typeof(string), table.Columns["CNES"]!.DataType);
        Assert.Equal(typeof(int), table.Columns["QT_EXIST"]!.DataType);
        Assert.All(table.Rows.Cast<DataRow>(), row => Assert.Equal("3", row["PF_PJ"]));
    }
}
