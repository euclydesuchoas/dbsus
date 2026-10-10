using Xunit;

namespace DbSus.Client.Tests;

// How a file name is read back into a state, a period and a part letter: the dataset's prefix, the
// abbreviation of a real state, the digits of the period, at most one part letter, the extension.
public class DatasusFileNameTests
{
    // Names in which the code of the file type is also the abbreviation of a state.
    [Theory]
    [InlineData("SIASUS", "PA", "PAPA2407.dbc", DatasusUf.PA, 2024, 7)]
    [InlineData("SIASUS", "AM", "AMAM2407.dbc", DatasusUf.AM, 2024, 7)]
    [InlineData("SIASUS", "AM", "AMPA2407.dbc", DatasusUf.PA, 2024, 7)]
    [InlineData("SIHSUS", "SP", "SPSP2407.dbc", DatasusUf.SP, 2024, 7)]
    [InlineData("SIHSUS", "RJ", "RJRJ2407.dbc", DatasusUf.RJ, 2024, 7)]
    [InlineData("SIHSUS", "ER", "ERPA2407.dbc", DatasusUf.PA, 2024, 7)]
    [InlineData("SIHSUS", "SP", "sppa1001.dbc", DatasusUf.PA, 2010, 1)]
    [InlineData("SIASUS", "AQ", "AQPA1001.DBC", DatasusUf.PA, 2010, 1)]
    [InlineData("SIASUS", "ATD", "ATDMG1507.dbc", DatasusUf.MG, 2015, 7)]
    [InlineData("CNES", "IN", "INTO2608.dbc", DatasusUf.TO, 2026, 8)]
    public void TryParseFileName_MonthlyName_ReadsStateAndMonth(string source, string code, string fileName, DatasusUf uf, int year, int month)
    {
        var era = DatasusCatalog.Get(source, code).CurrentEra;

        Assert.True(era.TryParseFileName(fileName, out var parsedUf, out var period, out var part));

        Assert.Equal(uf, parsedUf);
        Assert.Equal(DatasusPeriod.OfMonth(year, month), period);
        Assert.Null(part);
    }

    [Theory]
    [InlineData("SIM", "DO", "DORR2011.DBC", DatasusUf.RR, 2011)]
    [InlineData("SIM", "DO", "DOAC1996.dbc", DatasusUf.AC, 1996)]
    [InlineData("SINASC", "DN", "DNRR2010.DBC", DatasusUf.RR, 2010)]
    [InlineData("SINASC", "DN", "dnto2024.dbc", DatasusUf.TO, 2024)]
    public void TryParseFileName_YearlyName_ReadsStateAndYear(string source, string code, string fileName, DatasusUf uf, int year)
    {
        var era = DatasusCatalog.Get(source, code).CurrentEra;

        Assert.True(era.TryParseFileName(fileName, out var parsedUf, out var period, out var part));

        Assert.Equal(uf, parsedUf);
        Assert.Equal(DatasusPeriod.OfYear(year), period);
        Assert.Null(part);
    }

    [Theory]
    [InlineData("PAMG2407a.dbc", 'a')]
    [InlineData("PAMG2407b.dbc", 'b')]
    [InlineData("PASP2607d.dbc", 'd')]
    [InlineData("PASP2607D.DBC", 'd')]
    public void TryParseFileName_PartOfASplitTable_ReadsThePartLetterInLowerCase(string fileName, char expectedPart)
    {
        var era = DatasusCatalog.SiaSus.PA.CurrentEra;

        Assert.True(era.TryParseFileName(fileName, out var uf, out var period, out var part));

        Assert.Equal(fileName.StartsWith("PAMG", StringComparison.Ordinal) ? DatasusUf.MG : DatasusUf.SP, uf);
        Assert.Equal(DatasusPeriod.OfMonth(fileName.Contains("2407") ? 2024 : 2026, 7), period);
        Assert.Equal(expectedPart, part);
    }

    // Real names of other file types and of files outside the pattern, as listed in October 2026,
    // and names made up to probe the rule. None of them is a file of the dataset.
    [Theory]
    [InlineData("SIASUS", "AM", "AMPMG2306.dbc")]      // SIA-AMP, another file type: "PM" is not a state
    [InlineData("SIASUS", "AM", "AMPPA2306.dbc")]      // made up: AMP of Pará must not read as AM
    [InlineData("SIASUS", "AD", "ADMG1507.txt")]
    [InlineData("SIASUS", "PA", "PAMG2407_1.dbc")]     // the suffix of SIA-BI is not a part letter
    [InlineData("SIASUS", "PA", "PAMG2407ab.dbc")]
    [InlineData("SIASUS", "PA", "PAMG2407.dbc.bak")]
    [InlineData("SIASUS", "PA", "PAMG240.dbc")]
    [InlineData("SIASUS", "PA", "PAMG24O7.dbc")]
    [InlineData("SIASUS", "PA", "PAMG2413.dbc")]       // month 13
    [InlineData("SIASUS", "PA", "PAMG2400.dbc")]       // month 0
    [InlineData("SIASUS", "PA", "PAXX2407.dbc")]       // two letters that are not a state
    [InlineData("SIASUS", "PA", "PABR2407.dbc")]       // the whole country is not a state
    [InlineData("SIASUS", "PA", "ABOAC1502.dbc")]
    [InlineData("SIASUS", "PA", "IMPBORL.DLL")]
    [InlineData("SIASUS", "PA", "PA.dbc")]
    [InlineData("SIASUS", "PA", "")]
    [InlineData("SIHSUS", "RD", "RDAC2017.zip")]
    [InlineData("SIHSUS", "RD", "CHBR1901.dbc")]
    [InlineData("SIHSUS", "SP", "SPRR٢٤٠٧.dbc")]       // digits that are not ASCII
    [InlineData("CNES", "HB", "HBufAAmm.dbc")]         // a placeholder file in the CNES folders
    [InlineData("CNES", "LT", "LTSP0507.dbc")]         // the month before the folder layout starts
    [InlineData("SIM", "DO", "DOBR2011.dbc")]
    [InlineData("SIM", "DO", "DORRR95.DBC")]           // the older layout: other prefix, two-digit year
    [InlineData("SIM", "DO", "DORR1995.dbc")]          // a year before the folder layout starts
    [InlineData("SIM", "DO", "DORR0000.dbc")]
    [InlineData("SINASC", "DN", "DNEX2014.dbc")]
    [InlineData("SINASC", "DN", "DNEX25.dbc")]
    [InlineData("SINASC", "DN", "DNRRR1995.dbc")]
    public void TryParseFileName_NameOutsideThePattern_IsNotAFileOfTheDataset(string source, string code, string fileName)
    {
        var era = DatasusCatalog.Get(source, code).CurrentEra;

        Assert.False(era.TryParseFileName(fileName, out _, out _, out _));
    }

    [Fact]
    public void TryParseFileName_TwoDigitYear_IsReadWithinTheHundredYearsOfTheLayout()
    {
        var sia = DatasusCatalog.SiaSus.PA.CurrentEra;
        var cnes = DatasusCatalog.Cnes.ST.CurrentEra;

        Assert.True(sia.TryParseFileName("PAMG0801.dbc", out _, out var first, out _));
        Assert.Equal(DatasusPeriod.OfMonth(2008, 1), first);
        Assert.True(sia.TryParseFileName("PAMG9912.dbc", out _, out var last, out _));
        Assert.Equal(DatasusPeriod.OfMonth(2099, 12), last);
        Assert.True(cnes.TryParseFileName("STMG0508.dbc", out _, out var firstCnes, out _));
        Assert.Equal(DatasusPeriod.OfMonth(2005, 8), firstCnes);
    }

    [Fact]
    public void FileNamePattern_DescribesEachLayout()
    {
        Assert.Equal("PA{UF}{YY}{MM}.dbc", DatasusCatalog.SiaSus.PA.CurrentEra.FileNamePattern);
        Assert.Equal("ATD{UF}{YY}{MM}.dbc", DatasusCatalog.SiaSus.ATD.CurrentEra.FileNamePattern);
        Assert.Equal("DO{UF}{YYYY}.dbc", DatasusCatalog.Sim.DO.CurrentEra.FileNamePattern);
    }

    [Fact]
    public void EveryDataset_RoundTripsEveryStateThroughItsOwnNames()
    {
        foreach (var dataset in DatasusCatalog.Datasets)
        {
            var period = dataset.Periodicity == DatasusPeriodicity.Monthly ? DatasusPeriod.OfMonth(2024, 7) : DatasusPeriod.OfYear(2024);
            foreach (var uf in DatasusUfNames.All)
            {
                var path = dataset.GetPath(uf, period);
                var fileName = path.Substring(path.LastIndexOf('/') + 1);

                Assert.True(dataset.CurrentEra.TryParseFileName(fileName, out var parsedUf, out var parsedPeriod, out var part), fileName);
                Assert.Equal(uf, parsedUf);
                Assert.Equal(period, parsedPeriod);
                Assert.Null(part);

                // No other dataset of the same folder claims the name.
                foreach (var other in DatasusCatalog.Datasets.Where(other => other != dataset && other.CurrentEra.Directory == dataset.CurrentEra.Directory))
                {
                    Assert.False(other.CurrentEra.TryParseFileName(fileName, out _, out _, out _), $"{other} read {fileName}");
                }
            }
        }
    }
}
