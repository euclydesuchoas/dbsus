using Xunit;

namespace DbSus.Client.Tests;

// The file for the whole country of a dataset: its path, by folder layout, and the refusal of a
// dataset that has no such file, or that has no file by state. The file names are real ones, as
// the DATASUS server listed them in October 2026.
public class DatasusNationalPathTests
{
    [Theory]
    [InlineData("SIM", "DO", 2011, "/SIM/CID10/DORES/DOBR2011.dbc")]
    [InlineData("SIM", "DO", 1996, "/SIM/CID10/DORES/DOBR1996.dbc")]
    [InlineData("SIM", "DO", 1995, "/SIM/CID9/DORES/DORBR95.dbc")]
    [InlineData("SIM", "DO", 1979, "/SIM/CID9/DORES/DORBR79.dbc")]
    [InlineData("SINASC", "DN", 2010, "/SINASC/1996_/Dados/DNRES/DNBR2010.dbc")]
    [InlineData("SINASC", "DN", 1996, "/SINASC/1996_/Dados/DNRES/DNBR1996.dbc")]
    [InlineData("SINASC", "DN", 1995, "/SINASC/1994_1995/Dados/DNRES/DNBR1995.dbc")]
    [InlineData("SINASC", "DN", 1994, "/SINASC/1994_1995/Dados/DNRES/DNBR1994.dbc")]
    [InlineData("PCE", "PCE", 1995, "/PCE/Dados/PCEBR95.dbc")]
    [InlineData("PCE", "PCE", 2000, "/PCE/Dados/PCEBR00.dbc")]
    [InlineData("PCE", "PCE", 2026, "/PCE/Dados/PCEBR26.dbc")]
    [InlineData("RESP", "RESP", 2015, "/RESP/DADOS/RESPBR15.dbc")]
    [InlineData("RESP", "RESP", 2024, "/RESP/DADOS/RESPBR24.dbc")]
    public void GetNationalPath_BuildsThePathInTheLayoutOfThePeriod(string source, string code, int year, string expected)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfYear(year);

        Assert.Equal(expected, dataset.GetNationalPath(period));
        Assert.Equal(expected, dataset.GetNationalCandidatePaths(period)[0]);
    }

    [Fact]
    public void GetNationalCandidatePaths_AddsThePreliminaryFolder_WhenTheLayoutOfThePeriodHasOne()
    {
        Assert.Equal(
            ["/SIM/CID10/DORES/DOBR2025.dbc", "/SIM/PRELIM/DORES/DOBR2025.dbc"],
            DatasusCatalog.Sim.DO.GetNationalCandidatePaths(DatasusPeriod.OfYear(2025)));
        Assert.Equal(
            ["/SINASC/1996_/Dados/DNRES/DNBR2026.dbc", "/SINASC/PRELIM/DNRES/DNBR2026.dbc"],
            DatasusCatalog.Sinasc.DN.GetNationalCandidatePaths(DatasusPeriod.OfYear(2026)));
        Assert.Equal(["/SIM/CID9/DORES/DORBR95.dbc"], DatasusCatalog.Sim.DO.GetNationalCandidatePaths(DatasusPeriod.OfYear(1995)));
        Assert.Equal(["/PCE/Dados/PCEBR99.dbc"], DatasusCatalog.Pce.PCE.GetNationalCandidatePaths(DatasusPeriod.OfYear(1999)));
        Assert.Equal(
            ["/SIM/CID10/DOFET/DOFET24.dbc", "/SIM/PRELIM/DOFET/DOFET24.dbc"],
            DatasusCatalog.Sim.DOFET.GetNationalCandidatePaths(DatasusPeriod.OfYear(2024)));
    }

    [Theory]
    [InlineData("SIHSUS", "RD")]
    [InlineData("SIASUS", "PA")]
    [InlineData("CNES", "LT")]
    [InlineData("CIH", "CR")]
    [InlineData("SISMAMA", "MM")]
    public void GetNationalPath_DatasetWithNoFileForTheWholeCountry_ThrowsSayingWhichMethodsToUse(string source, string code)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var month = DatasusPeriod.OfMonth(2012, 7);

        var path = Assert.Throws<NotSupportedException>(() => dataset.GetNationalPath(month));
        var candidates = Assert.Throws<NotSupportedException>(() => dataset.GetNationalCandidatePaths(month));

        Assert.Equal($"{dataset} has one file per state and none for the whole country: use GetPath or GetCandidatePaths.", path.Message);
        Assert.Equal(path.Message, candidates.Message);
        Assert.Equal(DatasusCoverage.Uf, dataset.Coverage);

        // The coverage is checked before the period: a period that is not set is refused in the same way.
        Assert.Throws<NotSupportedException>(() => dataset.GetNationalPath(default));
    }

    [Theory]
    [InlineData("SIM", "DOFET")]
    [InlineData("SIM", "DOEXT")]
    [InlineData("SIM", "DOINF")]
    [InlineData("SIM", "DOMAT")]
    [InlineData("SIM", "DOREXT")]
    [InlineData("SINASC", "DNEX")]
    [InlineData("SIHSUS", "CH")]
    [InlineData("SIHSUS", "CM")]
    [InlineData("SIHSUS", "MT")]
    [InlineData("ESUSNOTIFICA", "DCCR")]
    [InlineData("PO", "PO")]
    public void GetPath_DatasetWithNoFileByState_ThrowsSayingWhichMethodsToUse(string source, string code)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = dataset.Periodicity == DatasusPeriodicity.Monthly ? DatasusPeriod.OfMonth(2024, 7) : DatasusPeriod.OfYear(2024);

        var path = Assert.Throws<NotSupportedException>(() => dataset.GetPath(DatasusUf.SP, period));
        var candidates = Assert.Throws<NotSupportedException>(() => dataset.GetCandidatePaths(DatasusUf.SP, period));

        Assert.Equal($"{dataset} has one file for the whole country and none by state: use GetNationalPath or GetNationalCandidatePaths.", path.Message);
        Assert.Equal(path.Message, candidates.Message);
        Assert.Equal(DatasusCoverage.National, dataset.Coverage);
        Assert.StartsWith(dataset.CurrentEra.Directory + "/", dataset.GetNationalPath(period), StringComparison.Ordinal);

        // The coverage is checked before the state and the period.
        Assert.Throws<NotSupportedException>(() => dataset.GetPath(default, default));
    }

    [Fact]
    public void GetNationalPath_ShapeOfThePeriod_IsCheckedAsForTheFileOfAState()
    {
        var sim = DatasusCatalog.Sim.DO;
        var pce = DatasusCatalog.Pce.PCE;

        Assert.Equal("period", Assert.Throws<ArgumentException>(() => sim.GetNationalPath(default)).ParamName);
        Assert.Contains("SIM-DO is published yearly", Assert.Throws<ArgumentException>(() => sim.GetNationalPath(DatasusPeriod.OfMonth(2011, 7))).Message, StringComparison.Ordinal);
        var before = Assert.Throws<ArgumentOutOfRangeException>(() => sim.GetNationalPath(DatasusPeriod.OfYear(1978)));
        Assert.Equal("period", before.ParamName);
        Assert.Contains("SIM-DO has no files before 1979 in the folder layouts the catalog knows.", before.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.GetNationalCandidatePaths(DatasusPeriod.OfYear(1978)));

        // Two digits of the year: the same limit as for the file of a state.
        var beyond = Assert.Throws<ArgumentOutOfRangeException>(() => pce.GetNationalPath(DatasusPeriod.OfYear(2095)));
        Assert.Contains("PCE-PCE file names carry two digits of the year, so the catalog builds them only up to 2094: 2095 would get the name of 1995.", beyond.Message, StringComparison.Ordinal);
        Assert.Equal("/PCE/Dados/PCEBR94.dbc", pce.GetNationalPath(DatasusPeriod.OfYear(2094)));
        Assert.Equal("/SIM/CID10/DORES/DOBR2096.dbc", sim.GetNationalPath(DatasusPeriod.OfYear(2096)));
    }

    // The name of a file for the whole country, read by the layout of its folder: no state, no
    // part letter, and a year.
    [Theory]
    [InlineData("SIM", "DO", 1, "DOBR2011.dbc", 2011)]
    [InlineData("SIM", "DO", 1, "dobr1996.DBC", 1996)]
    [InlineData("SIM", "DO", 0, "DORBR95.dbc", 1995)]
    [InlineData("SIM", "DO", 0, "DORBR79.dbc", 1979)]
    [InlineData("SINASC", "DN", 1, "DNBR2010.dbc", 2010)]
    [InlineData("SINASC", "DN", 0, "DNBR1994.dbc", 1994)]
    [InlineData("PCE", "PCE", 0, "PCEBR95.dbc", 1995)]
    [InlineData("PCE", "PCE", 0, "PCEBR26.dbc", 2026)]
    [InlineData("RESP", "RESP", 0, "RESPBR24.dbc", 2024)]
    public void TryParseNationalFileName_NameOfTheFileForTheWholeCountry_IsReadByItsLayout(string source, string code, int era, string fileName, int year)
    {
        var layout = DatasusCatalog.Get(source, code).Eras[era];

        Assert.True(layout.TryParseNationalFileName(fileName, out var period));
        Assert.Equal(DatasusPeriod.OfYear(year), period);
        Assert.False(layout.TryParseFileName(fileName, out _, out _, out _));
    }

    [Theory]
    [InlineData("SIM", "DO", 1, "DORR2011.dbc")]       // the file of a state
    [InlineData("SIM", "DO", 1, "DOBR2011a.dbc")]      // the file for the whole country has no part letter
    [InlineData("SIM", "DO", 1, "DOBR2011_1.dbc")]
    [InlineData("SIM", "DO", 1, "DOBR1995.dbc")]       // a year of the older layout, with the name of the one in use now
    [InlineData("SIM", "DO", 1, "DORBR95.dbc")]
    [InlineData("SIM", "DO", 0, "DOBR1995.dbc")]
    [InlineData("SIM", "DO", 0, "DORBR96.dbc")]
    [InlineData("SIM", "DO", 0, "DORRR95.DBC")]
    [InlineData("SINASC", "DN", 1, "DNBR1995.dbc")]
    [InlineData("SINASC", "DN", 0, "DNBR1996.dbc")]
    [InlineData("SINASC", "DN", 1, "DNEX2014.dbc")]
    [InlineData("PCE", "PCE", 0, "PCEPE95.dbc")]
    [InlineData("PCE", "PCE", 0, "PCEBR1995.dbc")]
    [InlineData("RESP", "RESP", 0, "RESPBR.dbc")]      // a file with no year in its name, which is not of the pattern
    [InlineData("RESP", "RESP", 0, "RESPBR2024.dbc")]
    [InlineData("RESP", "RESP", 0, "RESPAC21.dbc")]
    public void TryParseNationalFileName_OtherName_IsNotTheFileForTheWholeCountry(string source, string code, int era, string fileName)
    {
        Assert.False(DatasusCatalog.Get(source, code).Eras[era].TryParseNationalFileName(fileName, out _));
    }

    // The table under "Files for the whole country" in the paths guide: one row per layout of each
    // dataset that has such a file, saying what the catalog says, and what was checked of it.
    [Fact]
    public void PathsGuide_HasOneRowPerFileForTheWholeCountry_SayingWhatTheCatalogSays()
    {
        var guide = File.ReadAllLines(Path.Combine(DbSus.Tests.TestSupport.RepositoryPaths.Root, "docs", "datasus-paths.md"));
        // The file types that also have files by state; the ones for the whole country only are in
        // the tables of the catalog and, for their older layouts, in the table of the older layouts.
        var expected = DatasusCatalog.Datasets
            .Where(dataset => dataset.Coverage == (DatasusCoverage.Uf | DatasusCoverage.National))
            .SelectMany(dataset => dataset.Eras.Select(era => (Dataset: dataset, Era: era)))
            .ToList();
        var rows = guide
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length == 7 && expected.Any(item => NameInTheGuide(item.Dataset) == cells[1]))
            .ToList();

        Assert.Equal(6, expected.Count);
        Assert.Equal(expected.Count, rows.Count);
        foreach (var (cells, item) in rows.Zip(expected, (cells, item) => (cells, item)))
        {
            var era = item.Era;
            Assert.Equal(NameInTheGuide(item.Dataset), cells[1]);
            Assert.Equal(era.LastPeriod is { } last ? $"{era.FirstPeriod} to {last}" : $"from {era.FirstPeriod}", cells[2]);
            Assert.StartsWith($"`{era.Directory}`", cells[3], StringComparison.Ordinal);
            Assert.Equal(era.PreliminaryDirectory is null, cells[3] == $"`{era.Directory}`");
            if (era.PreliminaryDirectory is not null)
            {
                Assert.Contains($"`{era.PreliminaryDirectory}` for preliminary years", cells[3], StringComparison.Ordinal);
            }

            Assert.Equal($"`{era.NationalFileNamePattern}`", cells[4]);
            Assert.True(cells[5] is "read" or "header", $"{item.Dataset}: \"{cells[5]}\"");
        }
    }

    // The guide writes PCE and RESP, whose code is the name of the source, once.
    private static string NameInTheGuide(DatasusDataset dataset) => dataset.Source == dataset.Code ? dataset.Source : dataset.ToString();

    // Every layout of a dataset names the files that the coverage of the dataset promises.
    [Fact]
    public void EveryEra_NamesTheFilesThatTheCoverageOfItsDatasetSays()
    {
        foreach (var dataset in DatasusCatalog.Datasets)
        {
            foreach (var era in dataset.Eras)
            {
                Assert.Equal(dataset.Coverage.HasFlag(DatasusCoverage.Uf), era.StatePrefix is not null);
                Assert.Equal(dataset.Coverage.HasFlag(DatasusCoverage.National), era.NationalStem is not null);
            }
        }

        Assert.Equal(
            ["SIM-DO", "SINASC-DN", "PCE-PCE", "RESP-RESP"],
            DatasusCatalog.Datasets.Where(dataset => dataset.Coverage == (DatasusCoverage.Uf | DatasusCoverage.National)).Select(dataset => dataset.ToString()));
        Assert.Equal(72, DatasusCatalog.Datasets.Count(dataset => dataset.Coverage.HasFlag(DatasusCoverage.National)));
    }
}
