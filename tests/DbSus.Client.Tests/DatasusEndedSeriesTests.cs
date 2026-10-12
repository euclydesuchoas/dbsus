using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The seven monthly file types of series that have ended: CIH-CR, SISPRENATAL-PN, SISCOLO CC and
// HC, SISMAMA CM, HM and MM. The file names are real ones, as the DATASUS server listed them in
// October 2026.
public sealed class DatasusEndedSeriesTests : IDisposable
{
    private const string SisColo = "/SISCAN/SISCOLO4/Dados";
    private const string SisMama = "/SISCAN/SISMAMA/Dados";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    // One real file of each, by its path, and the same name read back.
    [Theory]
    [InlineData("CIH", "CR", DatasusUf.RO, 2010, 4, "/CIH/200801_201012/Dados/CRRO1004.dbc")]
    [InlineData("SISPRENATAL", "PN", DatasusUf.AL, 2012, 2, "/SISPRENATAL/201201_/Dados/PNAL1202.dbc")]
    [InlineData("SISCOLO", "CC", DatasusUf.AP, 2006, 6, "/SISCAN/SISCOLO4/Dados/CCAP0606.dbc")]
    [InlineData("SISCOLO", "HC", DatasusUf.RR, 2006, 4, "/SISCAN/SISCOLO4/Dados/HCRR0604.dbc")]
    [InlineData("SISMAMA", "CM", DatasusUf.PI, 2012, 1, "/SISCAN/SISMAMA/Dados/CMPI1201.dbc")]
    [InlineData("SISMAMA", "HM", DatasusUf.AC, 2009, 8, "/SISCAN/SISMAMA/Dados/HMAC0908.dbc")]
    [InlineData("SISMAMA", "MM", DatasusUf.DF, 2009, 9, "/SISCAN/SISMAMA/Dados/MMDF0909.dbc")]
    public void GetPath_BuildsThePathOfARealFile_AndItsNameIsReadBack(string source, string code, DatasusUf uf, int year, int month, string expected)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfMonth(year, month);

        Assert.Equal(expected, dataset.GetPath(uf, period));
        Assert.Equal([expected], dataset.GetCandidatePaths(uf, period));
        Assert.True(dataset.CurrentEra.TryParseFileName(expected.Substring(expected.LastIndexOf('/') + 1), out var parsedUf, out var parsedPeriod, out var part));
        Assert.Equal(uf, parsedUf);
        Assert.Equal(period, parsedPeriod);
        Assert.Null(part);
    }

    // Names of other file types of the same folder, of the same code in another source, and
    // outside the pattern.
    [Theory]
    [InlineData("SISCOLO", "CC", "HCAP0606.dbc")]
    [InlineData("SISCOLO", "HC", "CCAP0606.dbc")]
    [InlineData("SISMAMA", "CM", "HMPI1201.dbc")]
    [InlineData("SISMAMA", "CM", "MMPI1201.dbc")]
    [InlineData("SISMAMA", "CM", "CMBR1901.dbc")]      // the SIHSUS file with the same code, for the whole country
    [InlineData("SISMAMA", "HM", "CMPI1201.dbc")]
    [InlineData("SISMAMA", "MM", "HMPI1201.dbc")]
    [InlineData("SISMAMA", "MM", "MMPI120.dbc")]
    [InlineData("CIH", "CR", "CIHAAM2603.dbc")]
    [InlineData("CIH", "CR", "CRXX1004.dbc")]
    [InlineData("SISPRENATAL", "PN", "PNAL1213a.dbc")]
    public void TryParseFileName_NameOfAnotherFileType_IsNotAFileOfTheDataset(string source, string code, string fileName)
    {
        Assert.False(DatasusCatalog.Get(source, code).CurrentEra.TryParseFileName(fileName, out _, out _, out _));
    }

    // SISMAMA has a file type with the code that SIHSUS uses for another one. A dataset is looked
    // up by its source and its code together.
    [Fact]
    public void Get_SameCodeInAnotherSource_IsAnotherDataset()
    {
        var cytology = DatasusCatalog.Get("SISMAMA", "CM");

        Assert.Same(DatasusCatalog.SisMama.CM, cytology);
        Assert.Equal("SISMAMA-CM", cytology.ToString());
        Assert.Equal("/SISCAN/SISMAMA/Dados/CMPI1201.dbc", cytology.GetPath(DatasusUf.PI, DatasusPeriod.OfMonth(2012, 1)));
        Assert.NotSame(DatasusCatalog.SisColo.CC, DatasusCatalog.SisMama.CM);
        Assert.NotSame(DatasusCatalog.SisColo.HC, DatasusCatalog.SisMama.HM);

        // The code alone names nothing: with the source of another file type there is no such dataset.
        Assert.False(DatasusCatalog.TryGet("SISCOLO", "CM", out _));
        Assert.False(DatasusCatalog.TryGet("SISMAMA", "CC", out _));
        Assert.True(DatasusCatalog.TryGet("sismama", "cm", out var found));
        Assert.Same(cytology, found);
    }

    // The series have ended, and the catalog does not say so: it builds a path for any month from
    // the first period on, as it does for every file type, whether the file is there or not.
    [Theory]
    [InlineData("CIH", "CR", 2011, 5, "/CIH/200801_201012/Dados/CRSP1105.dbc")]
    [InlineData("CIH", "CR", 2024, 7, "/CIH/200801_201012/Dados/CRSP2407.dbc")]
    [InlineData("SISPRENATAL", "PN", 2015, 1, "/SISPRENATAL/201201_/Dados/PNSP1501.dbc")]
    [InlineData("SISCOLO", "CC", 2015, 11, "/SISCAN/SISCOLO4/Dados/CCSP1511.dbc")]
    [InlineData("SISMAMA", "MM", 2015, 8, "/SISCAN/SISMAMA/Dados/MMSP1508.dbc")]
    public void GetPath_MonthAfterTheLastFileOfTheSeries_IsStillBuilt(string source, string code, int year, int month, string expected)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.Equal(expected, dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(year, month)));
        Assert.Null(Assert.Single(dataset.Eras).LastPeriod);
    }

    [Theory]
    [InlineData("CIH", "CR", 2007, 12, "2008-01", 2107)]
    [InlineData("SISPRENATAL", "PN", 2011, 12, "2012-01", 2111)]
    [InlineData("SISCOLO", "CC", 2005, 12, "2006-01", 2105)]
    [InlineData("SISCOLO", "HC", 2005, 12, "2006-01", 2105)]
    [InlineData("SISMAMA", "CM", 2008, 12, "2009-01", 2108)]
    [InlineData("SISMAMA", "HM", 2008, 12, "2009-01", 2108)]
    [InlineData("SISMAMA", "MM", 2008, 12, "2009-01", 2108)]
    public void GetPath_OutsideThePeriodsOfTheDataset_IsRefused(string source, string code, int yearBefore, int monthBefore, string firstPeriod, int lastYear)
    {
        var dataset = DatasusCatalog.Get(source, code);

        var before = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(yearBefore, monthBefore)));
        var beyond = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(lastYear + 1, 1)));

        Assert.Equal(firstPeriod, dataset.FirstPeriod.ToString());
        Assert.Contains($"{dataset} has no files before {firstPeriod} in the folder layouts the catalog knows.", before.Message, StringComparison.Ordinal);
        Assert.Contains($"{dataset} file names carry two digits of the year, so the catalog builds them only up to {lastYear}:", beyond.Message, StringComparison.Ordinal);
        Assert.NotNull(dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(lastYear, 12)));
    }

    [Fact]
    public async Task ListTablesAsync_FolderSharedByFileTypes_GivesEachDatasetItsOwnTables()
    {
        Add(SisMama, ("CMPI1201.dbc", 1), ("HMPI1201.dbc", 2), ("MMPI1201.dbc", 3), ("MMDF1201.dbc", 4), ("CMPI1202.dbc", 9));
        Add(SisColo, ("CCAP0606.dbc", 5), ("HCAP0606.dbc", 6));
        var client = _fixture.Client();
        var month = DatasusPeriod.OfMonth(2012, 1);
        var june2006 = DatasusPeriod.OfMonth(2006, 6);

        var cm = await client.ListTablesAsync(DatasusCatalog.SisMama.CM, month, month, cancellationToken: Ct);
        var hm = await client.ListTablesAsync(DatasusCatalog.SisMama.HM, month, month, cancellationToken: Ct);
        var mm = await client.ListTablesAsync(DatasusCatalog.SisMama.MM, month, month, cancellationToken: Ct);
        var cc = await client.ListTablesAsync(DatasusCatalog.SisColo.CC, june2006, june2006, cancellationToken: Ct);
        var hc = await client.ListTablesAsync(DatasusCatalog.SisColo.HC, june2006, june2006, cancellationToken: Ct);

        Assert.Equal([SisMama + "/CMPI1201.dbc"], cm.Select(table => table.Path));
        Assert.Equal([SisMama + "/HMPI1201.dbc"], hm.Select(table => table.Path));
        Assert.Equal([SisMama + "/MMDF1201.dbc", SisMama + "/MMPI1201.dbc"], mm.Select(table => table.Path));
        Assert.Equal([SisColo + "/CCAP0606.dbc"], cc.Select(table => table.Path));
        Assert.Equal([SisColo + "/HCAP0606.dbc"], hc.Select(table => table.Path));
        Assert.Same(DatasusCatalog.SisMama.MM, mm[0].Dataset);
    }

    [Fact]
    public async Task FindTableAsync_AndOpenReaderAsync_WorkForADatasetOfANewSource()
    {
        _fixture.Server.Add("/CIH/200801_201012/Dados/CRRO1004.dbc", ClientFixture.Dbc("A1", "A2"));
        var client = _fixture.Client();
        var period = DatasusPeriod.OfMonth(2010, 4);

        var table = await client.FindTableAsync(DatasusCatalog.Cih.CR, DatasusUf.RO, period, Ct);
        using var reader = await client.OpenReaderAsync(DatasusCatalog.Cih.CR, DatasusUf.RO, period, cancellationToken: Ct);

        Assert.Equal("/CIH/200801_201012/Dados/CRRO1004.dbc", table.Path);
        Assert.False(table.IsPreliminary);
        Assert.True(reader.Read());
        Assert.Equal("A1", reader.GetString(0));

        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(DatasusCatalog.Cih.CR, DatasusUf.AP, period, Ct));
        Assert.Equal("The server has no CIH-CR table of AP for 2010-04: /CIH/200801_201012/Dados/CRAP1004.dbc does not exist, whole or in parts.", missing.Message);
    }

    private void Add(string directory, params (string Name, int Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            _fixture.Server.Add(directory + "/" + name, new byte[size]);
        }
    }
}
