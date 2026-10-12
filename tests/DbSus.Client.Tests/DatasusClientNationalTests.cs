using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

// Finding, listing and opening the table for the whole country of a dataset, against the fake
// server. The file names are real ones, as the DATASUS server listed them in October 2026.
public sealed class DatasusClientNationalTests : IDisposable
{
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private const string SimOlder = "/SIM/CID9/DORES";
    private const string PceFolder = "/PCE/Dados";
    private const string RespFolder = "/RESP/DADOS";

    // A file type with one file for the whole country and none by state. No file type of the
    // catalog is like that yet, so this one is built here; the folder and the name are real.
    private static readonly DatasusDataset CountryOnly = new(
        "SINAN",
        "DENG",
        "Dengue",
        DatasusCoverage.National,
        new DatasusEra(DatasusPeriod.OfYear(2000), null, "/SINAN/DADOS/FINAIS", "/SINAN/DADOS/PRELIM", null, "DENGBR", 2));

    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task FindNationalTableAsync_TableInTheFinalFolder_ReturnsItWithNoState_AndThePathAsListed()
    {
        _fixture.Server.Add(SimFinal + "/DOBR2011.DBC", ClientFixture.Dbc("A1"), FakeServer.Time(3));
        _fixture.Server.Add(SimFinal + "/DORR2011.dbc", ClientFixture.Dbc("X1"));
        var client = _fixture.Client();

        var table = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), Ct);

        Assert.Equal(SimFinal + "/DOBR2011.DBC", table.Path);
        Assert.Null(table.Uf);
        Assert.False(table.IsPreliminary);
        Assert.Same(DatasusCatalog.Sim.DO, table.Dataset);
        Assert.Equal(DatasusPeriod.OfYear(2011), table.Period);
        var part = Assert.Single(table.Parts);
        Assert.Equal(table.Path, part.Path);
        Assert.Equal(FakeServer.Time(3), part.LastModifiedUtc);
        Assert.Equal(part.Size, table.Size);
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task FindNationalTableAsync_LooksInThePreliminaryFolderAfterTheFinalOne_AndPrefersTheFinal()
    {
        _fixture.Server.Add(SimFinal + "/DOBR2024.dbc", ClientFixture.Dbc("F1"));
        _fixture.Server.Add(SimPreliminary + "/DOBR2024.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add(SimPreliminary + "/DOBR2026.dbc", ClientFixture.Dbc("P2"));
        var client = _fixture.Client();

        var inBoth = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), Ct);
        var onlyPreliminary = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2026), Ct);

        Assert.Equal((SimFinal + "/DOBR2024.dbc", false), (inBoth.Path, inBoth.IsPreliminary));
        Assert.Equal((SimPreliminary + "/DOBR2026.dbc", true), (onlyPreliminary.Path, onlyPreliminary.IsPreliminary));
        Assert.Null(onlyPreliminary.Uf);
    }

    [Fact]
    public async Task FindNationalTableAsync_OlderLayout_AndLayoutWithoutPreliminaryData_LookInOneFolder()
    {
        _fixture.Server.Add(SimOlder + "/DORBR79.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(PceFolder + "/PCEBR95.dbc", ClientFixture.Dbc("B1"));
        _fixture.Server.Add(RespFolder + "/RESPBR24.dbc", ClientFixture.Dbc("C1"));
        _fixture.Server.Add(RespFolder + "/RESPBR.dbc", ClientFixture.Dbc("X1"));
        var client = _fixture.Client();

        var sim = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(1979), Ct);
        var pce = await client.FindNationalTableAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1995), Ct);
        var resp = await client.FindNationalTableAsync(DatasusCatalog.Resp.RESP, DatasusPeriod.OfYear(2024), Ct);

        Assert.Equal(SimOlder + "/DORBR79.dbc", sim.Path);
        Assert.Equal(PceFolder + "/PCEBR95.dbc", pce.Path);
        Assert.Equal(RespFolder + "/RESPBR24.dbc", resp.Path);
        Assert.All(new[] { sim, pce, resp }, table => Assert.Null(table.Uf));
        Assert.Equal(3, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task FindNationalTableAsync_NoTable_ThrowsNamingThePathsTried()
    {
        _fixture.Server.Add(SimFinal + "/DORR2031.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(PceFolder + "/PCEPE99.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        var withPreliminary = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2031), Ct));
        var withoutPreliminary = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1999), Ct));

        Assert.Equal("The server has no SIM-DO table of the whole country for 2031, final or preliminary: neither /SIM/CID10/DORES/DOBR2031.dbc nor /SIM/PRELIM/DORES/DOBR2031.dbc exists.", withPreliminary.Message);
        Assert.Equal(SimFinal + "/DOBR2031.dbc", withPreliminary.FileName);
        Assert.Equal("The server has no PCE-PCE table of the whole country for 1999: /PCE/Dados/PCEBR99.dbc does not exist, whole or in parts.", withoutPreliminary.Message);
        Assert.Equal(PceFolder + "/PCEBR99.dbc", withoutPreliminary.FileName);
    }

    [Fact]
    public async Task OpenReaderAsync_Table_ReadsATableForTheWholeCountryFoundInThePreliminaryFolder()
    {
        _fixture.Server.Add(SimPreliminary + "/DOBR2026.dbc", ClientFixture.Dbc("P1", "P2"));
        var client = _fixture.Client();
        var table = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2026), Ct);

        using var reader = await client.OpenReaderAsync(table, cancellationToken: Ct);

        Assert.True(table.IsPreliminary);
        Assert.Equal(["P1", "P2"], Codes(reader));
    }

    [Fact]
    public async Task OpenNationalReaderAsync_OpensFinalDataOnly()
    {
        _fixture.Server.Add(SimFinal + "/DOBR2011.dbc", ClientFixture.Dbc("F1", "F2"));
        _fixture.Server.Add(SimPreliminary + "/DOBR2026.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add(PceFolder + "/PCEBR95.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct))
        {
            Assert.Equal(["F1", "F2"], Codes(reader));
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1995), cancellationToken: Ct))
        {
            Assert.Equal(["B1"], Codes(reader));
        }

        var transfers = _fixture.Server.Transfers;
        var onlyPreliminary = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2026), cancellationToken: Ct));
        var none = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2030), cancellationToken: Ct));

        Assert.Equal(
            "SIM-DO of the whole country for 2026 is not published as final data: only the preliminary table /SIM/PRELIM/DORES/DOBR2026.dbc exists, and this method opens final data only. "
            + "To read preliminary data, find the table with FindNationalTableAsync and pass it to OpenReaderAsync(DatasusTableInfo).",
            onlyPreliminary.Message);
        Assert.Equal(SimFinal + "/DOBR2026.dbc", onlyPreliminary.FileName);
        Assert.Equal("The server has no SIM-DO table of the whole country for 2030, final or preliminary: neither /SIM/CID10/DORES/DOBR2030.dbc nor /SIM/PRELIM/DORES/DOBR2030.dbc exists.", none.Message);
        Assert.Equal(transfers, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task ListNationalTablesAsync_ListsEachFolderOnce_LeavesOutTheFilesOfStatesAndOtherNames_OrderedByPeriod()
    {
        Add(SimOlder, ("DORBR95.dbc", 1), ("DORBR94.dbc", 9), ("DORRR95.DBC", 9), ("DOBR1995.dbc", 9));
        Add(SimFinal, ("DOBR2024.dbc", 3), ("DOBR1996.dbc", 2), ("DORR2024.dbc", 9), ("DOBR2024a.dbc", 9), ("DOBR24.dbc", 9), ("DOBR2024_1.dbc", 9), ("DOBRXXXX.dbc", 9), ("DORBR95.dbc", 9));
        Add(SimPreliminary, ("DOBR2024.dbc", 7), ("DOBR2025.dbc", 4), ("DORR2025.dbc", 9));
        _fixture.Server.Add(SimFinal + "/Sub/DOBR2000.dbc", new byte[9]);
        var client = _fixture.Client();

        var tables = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(1995), DatasusPeriod.OfYear(2026), Ct);

        Assert.Equal(
            [
                (SimOlder + "/DORBR95.dbc", "1995", false, 1L),
                (SimFinal + "/DOBR1996.dbc", "1996", false, 2L),
                (SimFinal + "/DOBR2024.dbc", "2024", false, 3L),
                (SimPreliminary + "/DOBR2025.dbc", "2025", true, 4L),
            ],
            tables.Select(table => (table.Path, table.Period.ToString(), table.IsPreliminary, table.Size)));
        Assert.All(tables, table =>
        {
            Assert.Null(table.Uf);
            Assert.Same(DatasusCatalog.Sim.DO, table.Dataset);
            Assert.Equal(table.Path, Assert.Single(table.Parts).Path);
        });
        Assert.Equal(3, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task ListNationalTablesAsync_TwoDigitYear_LeavesOutTheFileWithNoYear()
    {
        Add(RespFolder, ("RESPBR24.dbc", 2), ("RESPBR15.dbc", 1), ("RESPBR.dbc", 9), ("RESPAC24.dbc", 9));
        Add(PceFolder, ("PCEBR00.dbc", 4), ("PCEBR99.dbc", 3), ("PCEPE99.dbc", 9));
        var client = _fixture.Client();

        var resp = await client.ListNationalTablesAsync(DatasusCatalog.Resp.RESP, DatasusPeriod.OfYear(2015), DatasusPeriod.OfYear(2026), Ct);
        var pce = await client.ListNationalTablesAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1999), DatasusPeriod.OfYear(2000), Ct);

        Assert.Equal([RespFolder + "/RESPBR15.dbc", RespFolder + "/RESPBR24.dbc"], resp.Select(table => table.Path));
        Assert.Equal([(PceFolder + "/PCEBR99.dbc", "1999"), (PceFolder + "/PCEBR00.dbc", "2000")], pce.Select(table => (table.Path, table.Period.ToString())));
    }

    // The two kinds of table never come together: forgetting the state never opens the file for
    // the whole country.
    [Fact]
    public async Task ListTablesAsync_StillLeavesOutTheFileForTheWholeCountry()
    {
        Add(SimFinal, ("DOBR2024.dbc", 9), ("DORR2024.dbc", 1), ("DOSP2024.dbc", 2));
        Add(PceFolder, ("PCEBR99.dbc", 9), ("PCEPE99.dbc", 3));
        var client = _fixture.Client();

        var sim = await client.ListTablesAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), DatasusPeriod.OfYear(2024), cancellationToken: Ct);
        var pce = await client.ListTablesAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1999), DatasusPeriod.OfYear(1999), cancellationToken: Ct);

        Assert.Equal([SimFinal + "/DORR2024.dbc", SimFinal + "/DOSP2024.dbc"], sim.Select(table => table.Path));
        Assert.Equal([PceFolder + "/PCEPE99.dbc"], pce.Select(table => table.Path));
        Assert.All(sim.Concat(pce), table => Assert.NotNull(table.Uf));
    }

    [Fact]
    public async Task NationalMethods_DatasetWithNoFileForTheWholeCountry_AreRefused_AndTheServerIsNotAsked()
    {
        var client = _fixture.Client();
        var rd = DatasusCatalog.SihSus.RD;
        var month = DatasusPeriod.OfMonth(2024, 7);
        const string Message = "SIHSUS-RD has one file per state and none for the whole country: use FindTableAsync, OpenReaderAsync or ListTablesAsync.";

        var find = await Assert.ThrowsAsync<ArgumentException>(() => client.FindNationalTableAsync(rd, month, Ct));
        var open = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenNationalReaderAsync(rd, month, cancellationToken: Ct));
        var list = await Assert.ThrowsAsync<ArgumentException>(() => client.ListNationalTablesAsync(rd, month, month, Ct));

        Assert.All(new[] { find, open, list }, exception =>
        {
            Assert.Equal("dataset", exception.ParamName);
            Assert.StartsWith(Message, exception.Message, StringComparison.Ordinal);
        });
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task MethodsByState_DatasetWithNoFileByState_AreRefused_AndTheServerIsNotAsked()
    {
        _fixture.Server.Add("/SINAN/DADOS/FINAIS/DENGBR24.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        var year = DatasusPeriod.OfYear(2024);
        const string Message = "SINAN-DENG has one file for the whole country and none by state: use FindNationalTableAsync, OpenNationalReaderAsync or ListNationalTablesAsync.";

        var find = await Assert.ThrowsAsync<ArgumentException>(() => client.FindTableAsync(CountryOnly, DatasusUf.SP, year, Ct));
        var open = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(CountryOnly, DatasusUf.SP, year, cancellationToken: Ct));
        var list = await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(CountryOnly, year, year, cancellationToken: Ct));

        Assert.All(new[] { find, open, list }, exception =>
        {
            Assert.Equal("dataset", exception.ParamName);
            Assert.StartsWith(Message, exception.Message, StringComparison.Ordinal);
        });
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);

        // The same dataset is found by the methods for the whole country.
        var table = await client.FindNationalTableAsync(CountryOnly, year, Ct);
        Assert.Equal("/SINAN/DADOS/FINAIS/DENGBR24.dbc", table.Path);
        Assert.Null(table.Uf);
    }

    [Fact]
    public async Task NationalMethods_ShapeOfTheRequest_IsChecked_AndNothingIsListed()
    {
        var client = _fixture.Client();
        var sim = DatasusCatalog.Sim.DO;
        var year = DatasusPeriod.OfYear(2024);

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.FindNationalTableAsync(null!, year, Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenNationalReaderAsync(null!, year, cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ListNationalTablesAsync(null!, year, year, Ct));
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentException>(() => client.FindNationalTableAsync(sim, default, Ct))).ParamName);
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentException>(() => client.OpenNationalReaderAsync(sim, DatasusPeriod.OfMonth(2024, 7), cancellationToken: Ct))).ParamName);
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindNationalTableAsync(sim, DatasusPeriod.OfYear(1978), Ct))).ParamName);
        Assert.Equal("period", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindNationalTableAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(2095), Ct))).ParamName);
        Assert.Equal("from", (await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ListNationalTablesAsync(sim, DatasusPeriod.OfYear(1978), year, Ct))).ParamName);
        Assert.Equal("to", (await Assert.ThrowsAsync<ArgumentException>(() => client.ListNationalTablesAsync(sim, year, DatasusPeriod.OfMonth(2024, 7), Ct))).ParamName);
        var empty = await Assert.ThrowsAsync<ArgumentException>(() => client.ListNationalTablesAsync(sim, year, DatasusPeriod.OfYear(2023), Ct));
        Assert.Contains("2024 is after 2023", empty.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
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
