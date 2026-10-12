using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// PCE and RESP: yearly files by state whose names carry two digits of the year. The file names
// are real ones, as the DATASUS server listed them in October 2026.
public sealed class DatasusTwoDigitYearTests : IDisposable
{
    private const string PceFolder = "/PCE/Dados";
    private const string RespFolder = "/RESP/DADOS";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("PCE", "PCE", DatasusUf.PE, 1999, "/PCE/Dados/PCEPE99.dbc")]
    [InlineData("PCE", "PCE", DatasusUf.PE, 2000, "/PCE/Dados/PCEPE00.dbc")]
    [InlineData("PCE", "PCE", DatasusUf.MG, 1995, "/PCE/Dados/PCEMG95.dbc")]
    [InlineData("PCE", "PCE", DatasusUf.MG, 2026, "/PCE/Dados/PCEMG26.dbc")]
    [InlineData("RESP", "RESP", DatasusUf.AC, 2015, "/RESP/DADOS/RESPAC15.dbc")]
    [InlineData("RESP", "RESP", DatasusUf.AC, 2021, "/RESP/DADOS/RESPAC21.dbc")]
    public void GetPath_BuildsTheYearWithTwoDigits_AndTheNameIsReadBackAsThatYear(string source, string code, DatasusUf uf, int year, string expected)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfYear(year);

        Assert.Equal(expected, dataset.GetPath(uf, period));
        Assert.Equal([expected], dataset.GetCandidatePaths(uf, period));
        Assert.True(dataset.CurrentEra.TryParseFileName(expected.Substring(expected.LastIndexOf('/') + 1), out var parsedUf, out var parsedPeriod, out var part));
        Assert.Equal(uf, parsedUf);
        Assert.Equal(period, parsedPeriod);
        Assert.Null(part);
    }

    [Fact]
    public void TryParseFileName_TwoDigits_AreAYearOfTheHundredThatStartWithTheDataset()
    {
        var pce = DatasusCatalog.Pce.PCE.CurrentEra;
        var resp = DatasusCatalog.Resp.RESP.CurrentEra;

        Assert.True(pce.TryParseFileName("PCEPE99.dbc", out _, out var year, out _));
        Assert.Equal(DatasusPeriod.OfYear(1999), year);
        Assert.True(pce.TryParseFileName("PCEPE00.dbc", out _, out year, out _));
        Assert.Equal(DatasusPeriod.OfYear(2000), year);
        Assert.True(pce.TryParseFileName("pcepe94.DBC", out _, out year, out _));
        Assert.Equal(DatasusPeriod.OfYear(2094), year);
        Assert.True(resp.TryParseFileName("RESPAC15.dbc", out var uf, out year, out _));
        Assert.Equal(DatasusUf.AC, uf);
        Assert.Equal(DatasusPeriod.OfYear(2015), year);
        Assert.True(resp.TryParseFileName("RESPAC14.dbc", out _, out year, out _));
        Assert.Equal(DatasusPeriod.OfYear(2114), year);
    }

    // The files for the whole country, and names outside the pattern, are not files of a state.
    [Theory]
    [InlineData("PCE", "PCE", "PCEBR95.dbc")]
    [InlineData("PCE", "PCE", "PCEPE1999.dbc")]
    [InlineData("PCE", "PCE", "PCEPE9.dbc")]
    [InlineData("PCE", "PCE", "PCEXX99.dbc")]
    [InlineData("PCE", "PCE", "PCEPE9912.dbc")]
    [InlineData("RESP", "RESP", "RESPBR.dbc")]
    [InlineData("RESP", "RESP", "RESPBR24.dbc")]
    [InlineData("RESP", "RESP", "RESPAC2021.dbc")]
    [InlineData("RESP", "RESP", "RESAC21.dbc")]
    public void TryParseFileName_FileForTheWholeCountry_OrNameOutsideThePattern_IsNotAFileOfAState(string source, string code, string fileName)
    {
        Assert.False(DatasusCatalog.Get(source, code).CurrentEra.TryParseFileName(fileName, out _, out _, out _));
    }

    // A year one hundred years after one that the name can tell apart is refused, with the
    // message of a month in the same case.
    [Theory]
    [InlineData("PCE", "PCE", 2095, 2094, "1995")]
    [InlineData("PCE", "PCE", 2126, 2094, "2026")]
    [InlineData("RESP", "RESP", 2115, 2114, "2015")]
    public void GetPath_YearBeyondTheYearsTwoDigitsCanName_ThrowsSayingWhoseNameItWouldGet(string source, string code, int year, int lastYear, string namesake)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfYear(year);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.MG, period));

        Assert.Equal("period", exception.ParamName);
        Assert.Equal(period, exception.ActualValue);
        Assert.Contains($"{dataset} file names carry two digits of the year, so the catalog builds them only up to {lastYear}: {year} would get the name of {namesake}.", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetCandidatePaths(DatasusUf.MG, period));
        Assert.NotNull(dataset.GetPath(DatasusUf.MG, DatasusPeriod.OfYear(lastYear)));
    }

    [Fact]
    public void GetPath_ShapeOfThePeriod_IsChecked()
    {
        var pce = DatasusCatalog.Pce.PCE;
        var resp = DatasusCatalog.Resp.RESP;

        var before = Assert.Throws<ArgumentOutOfRangeException>(() => pce.GetPath(DatasusUf.MG, DatasusPeriod.OfYear(1994)));
        Assert.Contains("PCE-PCE has no files before 1995 in the folder layouts the catalog knows.", before.Message, StringComparison.Ordinal);
        Assert.Contains("RESP-RESP has no files before 2015", Assert.Throws<ArgumentOutOfRangeException>(() => resp.GetPath(DatasusUf.AC, DatasusPeriod.OfYear(2014))).Message, StringComparison.Ordinal);

        // A month, also one beyond the hundred years, is refused for its shape.
        Assert.Contains("PCE-PCE is published yearly", Assert.Throws<ArgumentException>(() => pce.GetPath(DatasusUf.MG, DatasusPeriod.OfMonth(1999, 7))).Message, StringComparison.Ordinal);
        Assert.Contains("RESP-RESP is published yearly", Assert.Throws<ArgumentException>(() => resp.GetPath(DatasusUf.AC, DatasusPeriod.OfMonth(2115, 1))).Message, StringComparison.Ordinal);
        Assert.Equal(DatasusCoverage.Uf | DatasusCoverage.National, pce.Coverage);
        Assert.Equal(DatasusCoverage.Uf | DatasusCoverage.National, resp.Coverage);
    }

    [Fact]
    public async Task ListTablesAsync_AcrossTheTurnOfTheCentury_OrdersByYear_AndLeavesOutTheFilesOfTheCountry()
    {
        Add(PceFolder, ("PCEPE00.dbc", 2), ("PCEPE99.dbc", 1), ("PCEAL00.dbc", 3), ("PCEBR99.dbc", 9), ("PCEBR00.dbc", 9), ("PCEPE98.dbc", 9), ("PCEPE01.dbc", 9));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1999), DatasusPeriod.OfYear(2000), cancellationToken: Ct);

        Assert.Equal(
            [(PceFolder + "/PCEPE99.dbc", "1999", 1L), (PceFolder + "/PCEAL00.dbc", "2000", 3L), (PceFolder + "/PCEPE00.dbc", "2000", 2L)],
            tables.Select(table => (table.Path, table.Period.ToString(), table.Size)));
        Assert.Equal(1, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindTableAsync_AndOpenReaderAsync_ReadTheFileOfAState()
    {
        _fixture.Server.Add(RespFolder + "/RESPAC21.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(RespFolder + "/RESPBR21.dbc", ClientFixture.Dbc("B1", "B2"));
        var client = _fixture.Client();
        var year = DatasusPeriod.OfYear(2021);

        var table = await client.FindTableAsync(DatasusCatalog.Resp.RESP, DatasusUf.AC, year, Ct);
        using var reader = await client.OpenReaderAsync(DatasusCatalog.Resp.RESP, DatasusUf.AC, year, cancellationToken: Ct);

        Assert.Equal(RespFolder + "/RESPAC21.dbc", table.Path);
        Assert.False(table.IsPreliminary);
        Assert.True(reader.Read());
        Assert.Equal("A1", reader.GetString(0));
        Assert.False(reader.Read());

        var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.FindTableAsync(DatasusCatalog.Resp.RESP, DatasusUf.AC, DatasusPeriod.OfYear(2115), Ct));
        Assert.Equal("period", refused.ParamName);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.Resp.RESP, DatasusUf.MT, year, Ct));
        Assert.Equal("The server has no RESP-RESP table of MT for 2021: /RESP/DADOS/RESPMT21.dbc does not exist, whole or in parts.", missing.Message);
    }

    private void Add(string directory, params (string Name, int Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            _fixture.Server.Add(directory + "/" + name, new byte[size]);
        }
    }
}
