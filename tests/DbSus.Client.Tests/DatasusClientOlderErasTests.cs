using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

// Finding, listing and opening tables of the older folder layouts, against the fake server. The
// file names are real ones, as the DATASUS server listed them in October 2026.
public sealed class DatasusClientOlderErasTests : IDisposable
{
    private const string Sia = "/SIASUS/200801_/Dados";
    private const string SiaOlder = "/SIASUS/199407_200712/Dados";
    private const string SihOlder = "/SIHSUS/199201_200712/Dados";
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private const string SimOlder = "/SIM/CID9/DORES";
    private const string SinascOlder = "/SINASC/1994_1995/Dados/DNRES";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListTablesAsync_RangeAcrossLayouts_ListsEachFolderOnce_OrderedByPeriodThenState()
    {
        Add(SiaOlder, ("PASP0712.dbc", 3), ("PAMG0711.dbc", 1), ("PAMG0712.dbc", 2), ("PAMG0710.dbc", 9));
        Add(Sia, ("PASP0801a.dbc", 5), ("PASP0801b.dbc", 6), ("PAMG0801.dbc", 4), ("PAMG0802.dbc", 9));

        // A name of the other layout in each folder: in the folder in use now, PAMG0712.dbc is
        // 2107-12, outside the range; in the older folder, PAMG0801.dbc is not a file of that layout.
        Add(Sia, ("PAMG0712.dbc", 70));
        Add(SiaOlder, ("PAMG0801.dbc", 80));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, DatasusPeriod.OfMonth(2007, 11), DatasusPeriod.OfMonth(2008, 1), cancellationToken: Ct);

        Assert.Equal(
            [
                (SiaOlder + "/PAMG0711.dbc", "2007-11", 1L),
                (SiaOlder + "/PAMG0712.dbc", "2007-12", 2L),
                (SiaOlder + "/PASP0712.dbc", "2007-12", 3L),
                (Sia + "/PAMG0801.dbc", "2008-01", 4L),
                (Sia + "/PASP0801.dbc", "2008-01", 11L),
            ],
            tables.Select(table => (table.Path, table.Period.ToString(), table.Size)));
        Assert.All(tables, table => Assert.False(table.IsPreliminary));
        Assert.Equal([SiaOlder + "/PAMG0711.dbc"], tables[0].Parts.Select(part => part.Path));
        Assert.Equal(["PASP0801a.dbc", "PASP0801b.dbc"], tables[4].Parts.Select(part => part.Name));
        Assert.Equal(2, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task ListTablesAsync_RangeInsideOneLayout_ListsOnlyItsFolder()
    {
        Add(SiaOlder, ("PAMG0712.dbc", 2), ("PAMG9407.dbc", 1));
        Add(Sia, ("PAMG0801.dbc", 4));
        var client = _fixture.Client();

        var older = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, DatasusPeriod.OfMonth(1994, 7), DatasusPeriod.OfMonth(2007, 12), cancellationToken: Ct);
        Assert.Equal([SiaOlder + "/PAMG9407.dbc", SiaOlder + "/PAMG0712.dbc"], older.Select(table => table.Path));
        Assert.Equal(1, _fixture.Server.ListCalls);

        var current = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, DatasusPeriod.OfMonth(2008, 1), DatasusPeriod.OfMonth(2008, 12), cancellationToken: Ct);
        Assert.Equal([Sia + "/PAMG0801.dbc"], current.Select(table => table.Path));
        Assert.Equal(2, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListTablesAsync_YearlyRangeAcrossLayouts_ReadsEachNameWithItsLayout_AndListsThePreliminaryFolderToo()
    {
        Add(SimOlder, ("DORRR94.DBC", 1), ("DORRR95.DBC", 2), ("DORSP95.DBC", 3), ("DORBR95.dbc", 9), ("DORR1996.dbc", 9));
        Add(SimFinal, ("DORR1996.dbc", 4), ("DORR1997.dbc", 5), ("DORRR95.DBC", 9), ("DOBR1996.dbc", 9));
        Add(SimPreliminary, ("DORR2025.dbc", 9));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(1995), DatasusPeriod.OfYear(1996), cancellationToken: Ct);

        Assert.Equal(
            [(SimOlder + "/DORRR95.DBC", DatasusUf.RR, "1995", 2L), (SimOlder + "/DORSP95.DBC", DatasusUf.SP, "1995", 3L), (SimFinal + "/DORR1996.dbc", DatasusUf.RR, "1996", 4L)],
            tables.Select(table => (table.Path, table.Uf!.Value, table.Period.ToString(), table.Size)));

        // The older folder, the final folder of the layout in use now, and its folder of preliminary data.
        Assert.Equal(3, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task ListTablesAsync_PeriodBeforeTheOldestLayout_IsRefused_AndNothingIsListed()
    {
        var client = _fixture.Client();

        var monthly = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListTablesAsync(DatasusCatalog.SiaSus.PA, DatasusPeriod.OfMonth(1994, 6), DatasusPeriod.OfMonth(2008, 1), cancellationToken: Ct));
        var yearly = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(1978), DatasusPeriod.OfYear(1996), cancellationToken: Ct));

        Assert.Equal("from", monthly.ParamName);
        Assert.Contains("SIASUS-PA has no files before 1994-07 in the folder layouts the catalog knows. See https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md", monthly.Message, StringComparison.Ordinal);
        Assert.Equal("from", yearly.ParamName);
        Assert.Contains("SIM-DO has no files before 1979 in the folder layouts the catalog knows.", yearly.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindTableAsync_OlderPeriod_FindsTheTableInTheOlderFolder_WithThePathAsListed()
    {
        _fixture.Server.Add(SimOlder + "/DORRR95.DBC", ClientFixture.Dbc("A1"), FakeServer.Time(3));
        _fixture.Server.Add(SinascOlder + "/DNRRR1995.dbc", ClientFixture.Dbc("B1"));
        _fixture.Server.Add(SihOlder + "/RDRR0707.dbc", ClientFixture.Dbc("C1"));
        var client = _fixture.Client();

        var sim = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(1995), Ct);
        var sinasc = await client.FindTableAsync(DatasusCatalog.Sinasc.DN, DatasusUf.RR, DatasusPeriod.OfYear(1995), Ct);
        var sih = await client.FindTableAsync(DatasusCatalog.SihSus.RD, DatasusUf.RR, DatasusPeriod.OfMonth(2007, 7), Ct);

        Assert.Equal(SimOlder + "/DORRR95.DBC", sim.Path);
        Assert.False(sim.IsPreliminary);
        Assert.Equal(DatasusPeriod.OfYear(1995), sim.Period);
        Assert.Equal(FakeServer.Time(3), Assert.Single(sim.Parts).LastModifiedUtc);
        Assert.Equal(SinascOlder + "/DNRRR1995.dbc", sinasc.Path);
        Assert.Equal(SihOlder + "/RDRR0707.dbc", sih.Path);
        Assert.Equal(DatasusPeriod.OfMonth(2007, 7), sih.Period);

        // One listing per table: an older layout has no folder of preliminary data to look in.
        Assert.Equal(3, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task FindTableAsync_OlderPeriodWithNoTable_ThrowsNamingTheOlderPathOnly()
    {
        _fixture.Server.Add(SimOlder + "/DORSP95.DBC", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(SimFinal + "/DORR1995.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(SimPreliminary + "/DORR1995.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(1995), Ct));

        Assert.Equal("The server has no SIM-DO table of RR for 1995: /SIM/CID9/DORES/DORRR95.dbc does not exist, whole or in parts.", exception.Message);
        Assert.Equal(SimOlder + "/DORRR95.dbc", exception.FileName);
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task OpenReaderAsync_OlderPeriod_ReadsTheTableOfTheOlderFolder()
    {
        _fixture.Server.Add(SiaOlder + "/PAMG0707.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/PAMG0707.dbc", ClientFixture.Dbc("X1"));
        _fixture.Server.Add(SimOlder + "/DORRR79.DBC", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2007, 7), cancellationToken: Ct))
        {
            Assert.Equal(["A1", "A2"], Codes(reader));
        }

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(1979), cancellationToken: Ct))
        {
            Assert.Equal(["B1"], Codes(reader));
        }

        Assert.Equal(2, _fixture.Server.Transfers);
    }

    private static List<string> Codes(DbfReader reader)
    {
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private void Add(string directory, params (string Name, int Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            _fixture.Server.Add(directory + "/" + name, new byte[size]);
        }
    }
}
