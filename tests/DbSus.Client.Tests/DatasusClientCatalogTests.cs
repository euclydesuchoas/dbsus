using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

// Finding and opening tables by dataset, state and period, against the fake server. The file
// names are real ones, as the DATASUS server listed them in October 2026.
public sealed class DatasusClientCatalogTests : IDisposable
{
    private const string Sia = "/SIASUS/200801_/Dados";
    private const string Sih = "/SIHSUS/200801_/Dados";
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private static readonly byte[] Empty = [];
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasusPeriod July2024 => DatasusPeriod.OfMonth(2024, 7);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListTablesAsync_GroupsParts_LeavesOutOtherFiles_WithOneListingAndNoOtherRequest()
    {
        Add(Sia, ("PAMG2407a.dbc", 30), ("PAMG2407b.dbc", 12), ("PASP2407c.dbc", 3), ("PASP2407a.dbc", 1), ("PASP2407b.dbc", 2), ("PARR2407.dbc", 7));

        // Other periods, other file types of the same folder, and files outside the pattern.
        Add(Sia, ("PAMG2406.dbc", 1), ("PARR2408.dbc", 1), ("ADMG2407.dbc", 1), ("AMPMG2407.dbc", 1), ("BIMG2407_1.dbc", 1), ("PAMG2407_1.dbc", 1), ("PABR2407.dbc", 1), ("IMPBORL.DLL", 1), ("PAMG2407.zip", 1));
        _fixture.Server.Add(Sia + "/Sub/PAAC2407.dbc", Empty);
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, July2024, July2024, cancellationToken: Ct);

        Assert.Equal([DatasusUf.MG, DatasusUf.RR, DatasusUf.SP], tables.Select(table => table.Uf));
        Assert.All(tables, table =>
        {
            Assert.Same(DatasusCatalog.SiaSus.PA, table.Dataset);
            Assert.Equal(July2024, table.Period);
            Assert.False(table.IsPreliminary);
            Assert.All(table.Parts, part => Assert.Null(part.LastModifiedUtc));
        });
        Assert.Equal(Sia + "/PAMG2407.dbc", tables[0].Path);
        Assert.Equal([Sia + "/PAMG2407a.dbc", Sia + "/PAMG2407b.dbc"], tables[0].Parts.Select(part => part.Path));
        Assert.Equal(42, tables[0].Size);
        Assert.Equal(Sia + "/PARR2407.dbc", tables[1].Path);
        Assert.Equal(["PARR2407.dbc"], tables[1].Parts.Select(part => part.Name));
        Assert.Equal(7, tables[1].Size);
        Assert.Equal(["PASP2407a.dbc", "PASP2407b.dbc", "PASP2407c.dbc"], tables[2].Parts.Select(part => part.Name));
        Assert.Equal(6, tables[2].Size);
        Assert.Equal(tables[2].Path, tables[2].ToString());
        Assert.Equal(1, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task ListTablesAsync_FiltersByRangeAndState_OrderedByPeriodThenState()
    {
        Add(Sia, ("PASP2405.dbc", 1), ("PAMG2406.dbc", 1), ("PAAC2406.dbc", 1), ("PASP2407a.dbc", 1), ("PASP2407b.dbc", 1), ("PAAC2407.dbc", 1), ("PAMG2408.dbc", 1), ("PAMG2409.dbc", 1));
        var client = _fixture.Client();
        var from = DatasusPeriod.OfMonth(2024, 6);
        var to = DatasusPeriod.OfMonth(2024, 8);

        var all = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, from, to, cancellationToken: Ct);
        var minas = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, from, to, DatasusUf.MG, Ct);
        var none = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, from, to, DatasusUf.RR, Ct);

        Assert.Equal(
            ["2024-06 AC", "2024-06 MG", "2024-07 AC", "2024-07 SP", "2024-08 MG"],
            all.Select(table => $"{table.Period} {table.Uf}"));
        Assert.Equal(["2024-06", "2024-08"], minas.Select(table => table.Period.ToString()));
        Assert.Empty(none);

        // The listing is reused: three calls, one LIST.
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListTablesAsync_KeepsTheLetterCaseOfTheListing()
    {
        Add(Sih, ("sppa1001.dbc", 1), ("SPSP1001.dbc", 1));
        Add(Sia, ("AQPA1001.DBC", 1));
        var client = _fixture.Client();
        var january2010 = DatasusPeriod.OfMonth(2010, 1);

        var sih = await client.ListTablesAsync(DatasusCatalog.SihSus.SP, january2010, january2010, cancellationToken: Ct);
        var sia = await client.ListTablesAsync(DatasusCatalog.SiaSus.AQ, january2010, january2010, cancellationToken: Ct);

        Assert.Equal([Sih + "/sppa1001.dbc", Sih + "/SPSP1001.dbc"], sih.Select(table => table.Path));
        Assert.Equal([DatasusUf.PA, DatasusUf.SP], sih.Select(table => table.Uf));
        Assert.Equal(Sia + "/AQPA1001.DBC", Assert.Single(sia).Path);
    }

    [Fact]
    public async Task ListTablesAsync_PartMissingInTheMiddle_ShowsThePartsListed()
    {
        // The listing does not judge a table; opening it does.
        Add(Sia, ("PAMG2407a.dbc", 1), ("PAMG2407c.dbc", 1));
        var client = _fixture.Client();

        var table = Assert.Single(await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, July2024, July2024, cancellationToken: Ct));

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407c.dbc"], table.Parts.Select(part => part.Name));
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, Ct));
    }

    [Fact]
    public async Task ListTablesAsync_YearlySource_MarksPreliminaryTables_AndPrefersTheFinalFolder()
    {
        Add(SimFinal, ("DORR2023.dbc", 1), ("DORR2024.dbc", 5), ("DOSP2024.dbc", 1), ("DOBR2024.dbc", 1), ("DORR2011.DBC", 1));
        Add(SimPreliminary, ("DORR2024.dbc", 9), ("DORR2025.dbc", 1), ("DORR2026.dbc", 1), ("DOBR2025.dbc", 1));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2026), DatasusUf.RR, Ct);

        Assert.Equal(
            [(SimFinal + "/DORR2024.dbc", false, 5L), (SimPreliminary + "/DORR2025.dbc", true, 1L), (SimPreliminary + "/DORR2026.dbc", true, 1L)],
            tables.Select(table => (table.Path, table.IsPreliminary, table.Size)));
        Assert.Equal(2, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task ListTablesAsync_ShapeOfTheRequest_IsChecked_AndNothingIsListed()
    {
        var client = _fixture.Client();
        var pa = DatasusCatalog.SiaSus.PA;

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ListTablesAsync(null!, July2024, July2024, cancellationToken: Ct));
        var empty = await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(pa, July2024, DatasusPeriod.OfMonth(2024, 6), cancellationToken: Ct));
        Assert.Contains("2024-07 is after 2024-06", empty.Message, StringComparison.Ordinal);
        Assert.Equal("from", (await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(pa, default, July2024, cancellationToken: Ct))).ParamName);
        Assert.Equal("to", (await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(pa, July2024, DatasusPeriod.OfYear(2024), cancellationToken: Ct))).ParamName);
        Assert.Equal("from", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListTablesAsync(pa, DatasusPeriod.OfMonth(1994, 6), July2024, cancellationToken: Ct))).ParamName);
        Assert.Equal("uf", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListTablesAsync(pa, July2024, July2024, (DatasusUf)34, Ct))).ParamName);
        Assert.Equal(0, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListTablesAsync_FolderOf100000Files_IsGroupedQuickly()
    {
        // The shape of the SIASUS folder: 14 file types, 27 states, month after month.
        string[] codes = ["PA", "AD", "AM", "AQ", "AR", "PS", "ATD", "ACF", "BI", "AB", "ABO", "SAD", "AMP", "AN"];
        var count = 0;
        var months = 0;
        for (var month = DatasusPeriod.OfMonth(2008, 1); count < 100_000; month = Next(month))
        {
            months++;
            foreach (var code in codes)
            {
                foreach (var uf in DatasusUfNames.All)
                {
                    _fixture.Server.Add($"{Sia}/{code}{uf}{month.Year % 100:D2}{month.Month:D2}.dbc", Empty);
                    count++;
                }
            }
        }

        var client = _fixture.Client();
        var from = DatasusPeriod.OfMonth(2008, 1);
        var to = DatasusPeriod.OfMonth(2099, 12);
        Assert.Equal(count, (await client.ListAsync(Sia, Ct)).Count);

        // The listing is in the client's cache now: what is measured is the grouping alone.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var tables = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, from, to, cancellationToken: Ct);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var one = await client.ListTablesAsync(DatasusCatalog.SiaSus.ATD, from, from, DatasusUf.SP, Ct);

        Assert.True(count >= 100_000, $"{count} entries");
        Assert.Equal(27 * months, tables.Count);
        Assert.Equal(Sia + "/PAAC0801.dbc", tables[0].Path);
        Assert.Equal(Sia + "/ATDSP0801.dbc", Assert.Single(one).Path);
        Assert.Equal(1, _fixture.Server.ListCalls);

        // Measured in October 2026: about 15 ms and 4.6 MB (some 650 bytes per table) on .NET 10,
        // 19 ms and 4.9 MB on .NET Framework 4.8.1. The limits are far above that: they catch a
        // change that makes the grouping quadratic or allocate for every entry of the folder.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Grouping {count:N0} entries took {watch.Elapsed.TotalMilliseconds:N0} ms.");
        Assert.True(allocated < 1_000L * tables.Count, $"Grouping {count:N0} entries into {tables.Count:N0} tables allocated {allocated:N0} bytes.");
        TestContext.Current.SendDiagnosticMessage($"{count:N0} entries, {tables.Count:N0} tables: {watch.Elapsed.TotalMilliseconds:N1} ms, {allocated:N0} bytes.");

        static DatasusPeriod Next(DatasusPeriod month) => month.Month == 12 ? DatasusPeriod.OfMonth(month.Year + 1, 1) : DatasusPeriod.OfMonth(month.Year, month.Month!.Value + 1);
    }

    [Fact]
    public async Task FindTableAsync_ReturnsThePathAsListed_WithExactSizeAndTime()
    {
        _fixture.Server.Add(SimFinal + "/DORR2011.DBC", ClientFixture.Dbc("A1"), FakeServer.Time(3));
        var client = _fixture.Client();

        var table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2011), Ct);

        Assert.Equal(SimFinal + "/DORR2011.DBC", table.Path);
        Assert.NotEqual(DatasusCatalog.Sim.DO.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(2011)), table.Path);
        Assert.False(table.IsPreliminary);
        Assert.Equal(DatasusUf.RR, table.Uf);
        Assert.Equal(DatasusPeriod.OfYear(2011), table.Period);
        var part = Assert.Single(table.Parts);
        Assert.Equal(table.Path, part.Path);
        Assert.Equal(FakeServer.Time(3), part.LastModifiedUtc);
        Assert.Equal(part.Size, table.Size);
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task FindTableAsync_SplitTable_ReturnsThePartsAndTheNameWithoutAPartLetter()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/PAMG2407b.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        var table = await client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, Ct);

        Assert.Equal(Sia + "/PAMG2407.dbc", table.Path);
        Assert.Equal([Sia + "/PAMG2407a.dbc", Sia + "/PAMG2407b.dbc"], table.Parts.Select(part => part.Path));
        Assert.All(table.Parts, part => Assert.NotNull(part.LastModifiedUtc));
        Assert.Equal(table.Parts.Sum(part => part.Size), table.Size);
    }

    [Fact]
    public async Task FindTableAsync_TriesTheFinalFolderAndThenThePreliminaryOne()
    {
        _fixture.Server.Add(SimFinal + "/DORR2024.dbc", ClientFixture.Dbc("F1"));
        _fixture.Server.Add(SimPreliminary + "/DORR2024.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add(SimPreliminary + "/DORR2025.dbc", ClientFixture.Dbc("P2"));
        var client = _fixture.Client();

        var closed = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2024), Ct);
        var open = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), Ct);

        Assert.Equal((SimFinal + "/DORR2024.dbc", false), (closed.Path, closed.IsPreliminary));
        Assert.Equal((SimPreliminary + "/DORR2025.dbc", true), (open.Path, open.IsPreliminary));
    }

    [Fact]
    public async Task FindTableAsync_NoTable_ThrowsNamingThePathsTried()
    {
        _fixture.Server.Add(SimFinal + "/DOSP2024.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        var yearly = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2031), Ct));
        var monthly = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.AR, DatasusUf.RR, July2024, Ct));

        Assert.Equal("The server has no SIM-DO table of RR for 2031, final or preliminary: neither /SIM/CID10/DORES/DORR2031.dbc nor /SIM/PRELIM/DORES/DORR2031.dbc exists.", yearly.Message);
        Assert.Equal(SimFinal + "/DORR2031.dbc", yearly.FileName);
        Assert.Equal("The server has no SIASUS-AR table of RR for 2024-07: /SIASUS/200801_/Dados/ARRR2407.dbc does not exist, whole or in parts.", monthly.Message);
        Assert.Equal(Sia + "/ARRR2407.dbc", monthly.FileName);
    }

    [Fact]
    public async Task FindTableAsync_ListingOutOfDate_StillFindsTheTableByAsking()
    {
        _fixture.Server.Add(SimFinal + "/DOSP2024.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        Assert.Empty(await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2024), DatasusUf.RR, Ct));
        _fixture.Server.Add(SimFinal + "/DORR2024.dbc", ClientFixture.Dbc("B1"));

        // The listing, still in the cache, does not show the new file; the server is asked for it.
        var table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2024), Ct);

        Assert.Equal(SimFinal + "/DORR2024.dbc", table.Path);
        Assert.False(table.IsPreliminary);
    }

    [Fact]
    public async Task FindTableAsync_ShapeOfTheRequest_IsChecked()
    {
        var client = _fixture.Client();

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.FindTableAsync(null!, DatasusUf.SP, July2024, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.PA, default, July2024, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.SP, DatasusPeriod.OfYear(2024), Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.SP, DatasusPeriod.OfMonth(1994, 6), Ct));
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    // A month whose two digits of the year would name the month one hundred years before it is
    // refused before anything is asked: the table of 2008-07 is there, and is not the one of 2108-07.
    [Fact]
    public async Task CatalogMethods_MonthBeyondTheYearsTwoDigitsCanName_IsRefused_AndTheServerIsNotAsked()
    {
        _fixture.Server.Add(Sih + "/RDSP0807.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var rd = DatasusCatalog.SihSus.RD;
        var beyond = DatasusPeriod.OfMonth(2108, 7);

        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindTableAsync(rd, DatasusUf.SP, beyond, Ct))).ParamName);
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.OpenReaderAsync(rd, DatasusUf.SP, beyond, cancellationToken: Ct))).ParamName);
        Assert.Equal("to", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListTablesAsync(rd, July2024, beyond, cancellationToken: Ct))).ParamName);
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    // A year given to a monthly dataset, beyond those years, is refused for its shape, as any
    // other year is: with ArgumentException itself, and before anything is asked.
    [Fact]
    public async Task CatalogMethods_YearGivenToAMonthlyDataset_BeyondTheYearsTwoDigitsCanName_IsRefusedForItsShape_AndTheServerIsNotAsked()
    {
        _fixture.Server.Add(Sih + "/RDSP0807.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var rd = DatasusCatalog.SihSus.RD;
        var year = DatasusPeriod.OfYear(2108);

        var find = await Assert.ThrowsAsync<ArgumentException>(() => client.FindTableAsync(rd, DatasusUf.SP, year, Ct));
        Assert.Equal("period", find.ParamName);
        Assert.Contains("SIHSUS-RD is published monthly", find.Message, StringComparison.Ordinal);
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(rd, DatasusUf.SP, year, cancellationToken: Ct))).ParamName);
        Assert.Equal("to", (await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(rd, July2024, year, cancellationToken: Ct))).ParamName);
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task OpenReaderAsync_ByCriteria_ReadsEveryPartAsOneTable()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/PAMG2407b.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        using var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct);

        Assert.Equal(["A1", "A2", "B1"], Codes(reader));
        Assert.Equal(2, reader.PartCount);
    }

    [Fact]
    public async Task OpenReaderAsync_ByCriteria_UsesTheNameAsListed_SoTheCacheHasOneEntry()
    {
        _fixture.Server.Add(SimFinal + "/DORR2011.DBC", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2011), cancellationToken: Ct))
        {
            Assert.Equal(["A1"], Codes(reader));
        }

        var local = await client.DownloadAsync(SimFinal + "/DORR2011.DBC", Ct);

        Assert.Contains(Path.Combine("SIM", "CID10", "DORES", "DORR2011.DBC"), local, StringComparison.Ordinal);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task OpenReaderAsync_ByCriteria_OnlyPreliminaryExists_ThrowsExplaining_AndDownloadsNothing()
    {
        _fixture.Server.Add(SimPreliminary + "/DORR2025.dbc", ClientFixture.Dbc("P1"));
        var client = _fixture.Client();

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), cancellationToken: Ct));

        Assert.Equal(
            "SIM-DO of RR for 2025 is not published as final data: only the preliminary table /SIM/PRELIM/DORES/DORR2025.dbc exists, and this method opens final data only. "
            + "To read preliminary data, find the table with FindTableAsync and pass it to OpenReaderAsync(DatasusTableInfo).",
            exception.Message);
        Assert.Equal(SimFinal + "/DORR2025.dbc", exception.FileName);
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task OpenReaderAsync_ByCriteria_FinalAndPreliminaryExist_ReadsTheFinalOne()
    {
        _fixture.Server.Add(SimFinal + "/DORR2024.dbc", ClientFixture.Dbc("F1"));
        _fixture.Server.Add(SimPreliminary + "/DORR2024.dbc", ClientFixture.Dbc("P1"));
        var client = _fixture.Client();

        using var reader = await client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2024), cancellationToken: Ct);

        Assert.Equal(["F1"], Codes(reader));
    }

    [Fact]
    public async Task OpenReaderAsync_ByCriteria_NoTable_Throws()
    {
        var client = _fixture.Client();

        var yearly = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(DatasusCatalog.Sinasc.DN, DatasusUf.SP, DatasusPeriod.OfYear(2031), cancellationToken: Ct));
        var monthly = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(DatasusCatalog.Cnes.LT, DatasusUf.SP, DatasusPeriod.OfMonth(2005, 8), cancellationToken: Ct));

        Assert.Contains("final or preliminary: neither /SINASC/1996_/Dados/DNRES/DNSP2031.dbc nor /SINASC/PRELIM/DNRES/DNSP2031.dbc exists", yearly.Message, StringComparison.Ordinal);
        Assert.Contains("/CNES/200508_/Dados/LT/LTSP0508.dbc does not exist, whole or in parts", monthly.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(DatasusCatalog.Cnes.LT, DatasusUf.SP, DatasusPeriod.OfYear(2024), cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenReaderAsync((DatasusDataset)null!, DatasusUf.SP, July2024, cancellationToken: Ct));
    }

    [Fact]
    public async Task OpenReaderAsync_Table_ReadsPreliminaryData_WithoutLookingForTheTableAgain()
    {
        _fixture.Server.Add(SimPreliminary + "/DORR2025.dbc", ClientFixture.Dbc("P1", "P2"));
        var client = _fixture.Client();
        var table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), Ct);
        var listings = _fixture.Server.ListCalls;
        var requests = _fixture.Server.InfoCalls;

        using var reader = await client.OpenReaderAsync(table, cancellationToken: Ct);

        Assert.True(table.IsPreliminary);
        Assert.Equal(["P1", "P2"], Codes(reader));
        Assert.Equal(listings, _fixture.Server.ListCalls);

        // Only the check that the file is still the same version, after the transfer.
        Assert.Equal(requests + 1, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task OpenReaderAsync_Table_FromAListing_OpensThePartsListed()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Sia + "/PAMG2407b.dbc", ClientFixture.Dbc("B1", "B2"));
        var client = _fixture.Client();
        var table = Assert.Single(await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, July2024, July2024, DatasusUf.MG, Ct));

        using var reader = await client.OpenReaderAsync(table, cancellationToken: Ct);

        Assert.Equal(["A1", "B1", "B2"], Codes(reader));
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task OpenReaderAsync_Table_PartGoneFromTheServer_Throws()
    {
        _fixture.Server.Add(Sia + "/PARR2407.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var table = Assert.Single(await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, July2024, July2024, cancellationToken: Ct));
        _fixture.Server.Remove(Sia + "/PARR2407.dbc");

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(table, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenReaderAsync((DatasusTableInfo)null!, cancellationToken: Ct));
    }

    [Fact]
    public async Task CatalogMethods_AfterDispose_Throw()
    {
        _fixture.Server.Add(Sia + "/PARR2407.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var table = await client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.RR, July2024, Ct);
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListTablesAsync(DatasusCatalog.SiaSus.PA, July2024, July2024, cancellationToken: Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.RR, July2024, Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.RR, July2024, cancellationToken: Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenReaderAsync(table, cancellationToken: Ct));
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

    // Files with the given names and sizes; their content is never read.
    private void Add(string directory, params (string Name, int Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            _fixture.Server.Add(directory + "/" + name, new byte[size]);
        }
    }
}
