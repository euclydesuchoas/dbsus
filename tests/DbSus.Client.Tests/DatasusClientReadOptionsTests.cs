using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// The client adds nothing to the reader: the DbfReadOptions given to any of the methods that open
// a reader reach it. Shown with DbfReadOptions.UnconvertibleValues, on a made-up table with a
// numeric field of asterisks and a date with slashes.
public sealed class DatasusClientReadOptionsTests : IDisposable
{
    private const string Hint = "; to read such a value as null, see DbfReadOptions.UnconvertibleValues.";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task OpenNationalReaderAsync_PassesTheReadOptionsToTheReader()
    {
        _fixture.Server.Add("/painel_oncologia/Dados/POBR2013.dbc", Table());
        var client = _fixture.Client();
        var year = DatasusPeriod.OfYear(2013);

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Po.PO, year, cancellationToken: Ct))
        {
            Assert.True(reader.Read());
            Assert.Equal("Field QTY (N) of record 1 (POBR2013.dbc) does not hold a number" + Hint, Assert.Throws<FormatException>(() => reader.GetValue(1)).Message);
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Po.PO, year, Options(DbfUnconvertibleValueHandling.AsterisksAsNull), cancellationToken: Ct))
        {
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(1));
            Assert.Equal(DBNull.Value, reader.GetValue(1));
            Assert.Throws<FormatException>(() => reader.GetValue(2));
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Po.PO, year, Options(DbfUnconvertibleValueHandling.Null), cancellationToken: Ct))
        {
            Assert.Equal([["A1", DBNull.Value, DBNull.Value], ["A2", 7, new DateTime(2024, 1, 31)]], Rows(reader));
        }
    }

    [Fact]
    public async Task OpenReaderAsync_ByDatasetByTableAndByPath_PassTheReadOptionsToTheReader()
    {
        const string Path = "/SIHSUS/200801_/Dados/RDRR2206.dbc";
        _fixture.Server.Add(Path, Table());
        var client = _fixture.Client();
        var month = DatasusPeriod.OfMonth(2022, 6);
        object[][] expected = [["A1", DBNull.Value, DBNull.Value], ["A2", 7, new DateTime(2024, 1, 31)]];

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.SihSus.RD, DatasusUf.RR, month, Options(DbfUnconvertibleValueHandling.Null), cancellationToken: Ct))
        {
            Assert.Equal(expected, Rows(reader));
        }

        var table = await client.FindTableAsync(DatasusCatalog.SihSus.RD, DatasusUf.RR, month, Ct);
        using (var reader = await client.OpenReaderAsync(table, Options(DbfUnconvertibleValueHandling.Null), cancellationToken: Ct))
        {
            Assert.Equal(expected, Rows(reader));
        }

        using (var reader = await client.OpenReaderAsync(Path, Options(DbfUnconvertibleValueHandling.Null), cancellationToken: Ct))
        {
            Assert.Equal(expected, Rows(reader));
        }

        // Without the option, the three open the table and the reader throws on the value.
        using (var reader = await client.OpenReaderAsync(Path, cancellationToken: Ct))
        {
            Assert.True(reader.Read());
            Assert.Throws<FormatException>(() => reader.GetValue(1));
            Assert.False(reader.IsDBNull(1));
        }
    }

    private static DbfReadOptions Options(DbfUnconvertibleValueHandling mode) => new() { UnconvertibleValues = mode };

    private static byte[] Table() => SyntheticDbc.BuildDbc(new DbfBuilder()
        .Field("CODE", 'C', 4).Field("QTY", 'N', 4).Field("WHEN", 'D', 8)
        .Record("A1", "****", "31/01/24")
        .Record("A2", "   7", "20240131")
        .Build());

    private static List<object[]> Rows(DbfReader reader)
    {
        var rows = new List<object[]>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            reader.GetValues(values);
            rows.Add(values);
        }

        return rows;
    }
}
