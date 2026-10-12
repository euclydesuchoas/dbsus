using Xunit;

namespace DbSus.Client.Tests;

// The older folder layouts of the six datasets that have one: paths, file names read back, and
// the hundred years of a two-digit year, which are counted in each layout on its own. The file
// names are real ones, as the DATASUS server listed them in October 2026.
public class DatasusOlderErasTests
{
    private const DatasusCoverage UfAndNational = DatasusCoverage.Uf | DatasusCoverage.National;

    // The older folder layouts, one row per dataset that has one: its periods, its folder (which
    // has no folder of preliminary data), its name pattern, and the first period of the layout in
    // use now. The first period is the start of the folder, not the first period with data.
    [Theory]
    [InlineData("SIASUS", "PA", "1994-07", "2007-12", "/SIASUS/199407_200712/Dados", "PA{UF}{YY}{MM}.dbc", "2008-01")]
    [InlineData("SIHSUS", "RD", "1992-01", "2007-12", "/SIHSUS/199201_200712/Dados", "RD{UF}{YY}{MM}.dbc", "2008-01")]
    [InlineData("SIHSUS", "SP", "1992-01", "2007-12", "/SIHSUS/199201_200712/Dados", "SP{UF}{YY}{MM}.dbc", "2008-01")]
    [InlineData("SIHSUS", "RJ", "1992-01", "2007-12", "/SIHSUS/199201_200712/Dados", "RJ{UF}{YY}{MM}.dbc", "2008-01")]
    [InlineData("SIM", "DO", "1979", "1995", "/SIM/CID9/DORES", "DOR{UF}{YY}.dbc", "1996")]
    [InlineData("SINASC", "DN", "1994", "1995", "/SINASC/1994_1995/Dados/DNRES", "DNR{UF}{YYYY}.dbc", "1996")]
    public void Dataset_OlderEra_IsAsTheCatalogTableSays(string source, string code, string firstPeriod, string lastPeriod, string directory, string fileNamePattern, string firstOfTheCurrentEra)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.Equal(2, dataset.Eras.Count);
        var older = dataset.Eras[0];
        Assert.Equal(firstPeriod, older.FirstPeriod.ToString());
        Assert.Equal(lastPeriod, older.LastPeriod.ToString());
        Assert.Equal(directory, older.Directory);
        Assert.Null(older.PreliminaryDirectory);
        Assert.Null(older.NationalStem);
        Assert.Equal(fileNamePattern, older.FileNamePattern);
        Assert.Equal(firstOfTheCurrentEra, dataset.CurrentEra.FirstPeriod.ToString());
        Assert.Equal(firstPeriod, dataset.FirstPeriod.ToString());
    }

    [Fact]
    public void Datasets_WithAnOlderEra_AreSix_AndTheOthersHaveOne()
    {
        var withOlder = DatasusCatalog.Datasets.Where(dataset => dataset.Eras.Count > 1).Select(dataset => dataset.ToString());

        Assert.Equal(["SIASUS-PA", "SIHSUS-RD", "SIHSUS-SP", "SIHSUS-RJ", "SIM-DO", "SINASC-DN"], withOlder);
        Assert.All(DatasusCatalog.Datasets, dataset => Assert.InRange(dataset.Eras.Count, 1, 2));

        // The eras of a dataset follow one another: no period is left out and none is in two.
        foreach (var dataset in DatasusCatalog.Datasets.Where(dataset => dataset.Eras.Count > 1))
        {
            var last = dataset.Eras[0].LastPeriod!.Value;
            var next = last.Month is not { } month ? DatasusPeriod.OfYear(last.Year + 1)
                : month == 12 ? DatasusPeriod.OfMonth(last.Year + 1, 1)
                : DatasusPeriod.OfMonth(last.Year, month + 1);
            Assert.Equal(next, dataset.CurrentEra.FirstPeriod);
        }
    }

    // The table under "Older layouts" in the paths guide: one row per older era, saying what the
    // catalog says, and what was checked of it: a file with records read whole, or a header alone.
    [Fact]
    public void PathsGuide_HasOneRowPerOlderEra_SayingWhatTheCatalogSays()
    {
        var guide = File.ReadAllLines(Path.Combine(DbSus.Tests.TestSupport.RepositoryPaths.Root, "docs", "datasus-paths.md"));
        var withOlder = DatasusCatalog.Datasets.Where(dataset => dataset.Eras.Count > 1).ToList();
        var rows = guide
            .Select(line => line.Split('|').Select(cell => cell.Trim()).ToArray())
            .Where(cells => cells.Length == 8 && withOlder.Any(dataset => dataset.ToString() == cells[1]))
            .ToList();

        Assert.Equal(withOlder.Count, rows.Count);
        foreach (var (cells, dataset) in rows.Zip(withOlder, (cells, dataset) => (cells, dataset)))
        {
            var older = dataset.Eras[0];
            Assert.Equal(dataset.ToString(), cells[1]);
            Assert.Equal(older.FirstPeriod.ToString(), cells[2]);
            Assert.Equal(older.LastPeriod.ToString(), cells[3]);
            Assert.Equal($"`{older.Directory}`", cells[4]);
            Assert.Equal($"`{older.FileNamePattern}`", cells[5]);
            Assert.True(cells[6] is "read" or "header", $"{dataset}: \"{cells[6]}\"");
        }
    }

    // The paths that the file transfer page of the DATASUS website returned for older periods in
    // October 2026. It returned the SIM one with the extension in upper case, as the server lists it.
    [Theory]
    [InlineData("SIASUS", "PA", DatasusUf.MG, 2007, 7, "/SIASUS/199407_200712/Dados/PAMG0707.dbc")]
    [InlineData("SIHSUS", "RD", DatasusUf.RR, 2007, 7, "/SIHSUS/199201_200712/Dados/RDRR0707.dbc")]
    [InlineData("SIHSUS", "SP", DatasusUf.RR, 2007, 7, "/SIHSUS/199201_200712/Dados/SPRR0707.dbc")]
    [InlineData("SIM", "DO", DatasusUf.RR, 1995, 0, "/SIM/CID9/DORES/DORRR95.DBC")]
    [InlineData("SINASC", "DN", DatasusUf.RR, 1995, 0, "/SINASC/1994_1995/Dados/DNRES/DNRRR1995.dbc")]
    public void GetPath_PeriodOfAnOlderLayout_IsThePathTheWebsiteReturned(string source, string code, DatasusUf uf, int year, int month, string returned)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month);

        var path = dataset.GetPath(uf, period);

        Assert.Equal(returned, path, ignoreCase: true);
        Assert.EndsWith(".dbc", path, StringComparison.Ordinal);

        // An older layout has no folder of preliminary data: one candidate.
        Assert.Equal([path], dataset.GetCandidatePaths(uf, period));
    }

    // The last period of each older layout and the first of the one in use now.
    [Theory]
    [InlineData("SIASUS", "PA", 1994, 7, "/SIASUS/199407_200712/Dados/PASP9407.dbc")]
    [InlineData("SIASUS", "PA", 2007, 12, "/SIASUS/199407_200712/Dados/PASP0712.dbc")]
    [InlineData("SIASUS", "PA", 2008, 1, "/SIASUS/200801_/Dados/PASP0801.dbc")]
    [InlineData("SIHSUS", "RD", 1992, 1, "/SIHSUS/199201_200712/Dados/RDSP9201.dbc")]
    [InlineData("SIHSUS", "RD", 2007, 12, "/SIHSUS/199201_200712/Dados/RDSP0712.dbc")]
    [InlineData("SIHSUS", "RD", 2008, 1, "/SIHSUS/200801_/Dados/RDSP0801.dbc")]
    [InlineData("SIHSUS", "SP", 1992, 1, "/SIHSUS/199201_200712/Dados/SPSP9201.dbc")]
    [InlineData("SIHSUS", "RJ", 1992, 1, "/SIHSUS/199201_200712/Dados/RJSP9201.dbc")]
    [InlineData("SIHSUS", "RJ", 2008, 1, "/SIHSUS/200801_/Dados/RJSP0801.dbc")]
    [InlineData("SIM", "DO", 1979, 0, "/SIM/CID9/DORES/DORSP79.dbc")]
    [InlineData("SIM", "DO", 1995, 0, "/SIM/CID9/DORES/DORSP95.dbc")]
    [InlineData("SIM", "DO", 1996, 0, "/SIM/CID10/DORES/DOSP1996.dbc")]
    [InlineData("SINASC", "DN", 1994, 0, "/SINASC/1994_1995/Dados/DNRES/DNRSP1994.dbc")]
    [InlineData("SINASC", "DN", 1995, 0, "/SINASC/1994_1995/Dados/DNRES/DNRSP1995.dbc")]
    [InlineData("SINASC", "DN", 1996, 0, "/SINASC/1996_/Dados/DNRES/DNSP1996.dbc")]
    public void GetPath_AtTheEdgesOfAnOlderLayout_UsesTheFolderAndTheNameOfEachLayout(string source, string code, int year, int month, string expected)
    {
        var period = month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month);

        Assert.Equal(expected, DatasusCatalog.Get(source, code).GetPath(DatasusUf.SP, period));
    }

    [Fact]
    public void GetCandidatePaths_OlderYearOfSim_HasNoPreliminaryCandidate_AndTheFirstYearOfTheCurrentLayoutHas()
    {
        var sim = DatasusCatalog.Sim.DO;

        Assert.Equal(["/SIM/CID9/DORES/DORRR95.dbc"], sim.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(1995)));
        Assert.Equal(["/SIM/CID10/DORES/DORR1996.dbc", "/SIM/PRELIM/DORES/DORR1996.dbc"], sim.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(1996)));
    }

    // The hundred years that two digits can name are counted in each layout: the same name is a
    // month of 2007 in the older folder and the same month of 2107 in the one in use now.
    [Fact]
    public void GetPath_TwoDigitYear_NamesAYearOfItsOwnLayout()
    {
        var rd = DatasusCatalog.SihSus.RD;

        Assert.Equal("/SIHSUS/199201_200712/Dados/RDSP0712.dbc", rd.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2007, 12)));
        Assert.Equal("/SIHSUS/200801_/Dados/RDSP0712.dbc", rd.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2107, 12)));

        Assert.True(rd.Eras[0].TryParseFileName("RDSP0712.dbc", out _, out var older, out _));
        Assert.Equal(DatasusPeriod.OfMonth(2007, 12), older);
        Assert.True(rd.CurrentEra.TryParseFileName("RDSP0712.dbc", out _, out var current, out _));
        Assert.Equal(DatasusPeriod.OfMonth(2107, 12), current);
    }

    // The limit of the hundred years is the one of the layout in use now, whatever came before it.
    [Theory]
    [InlineData("SIASUS", "PA", 2108, 1, 2107, "2008-01")]
    [InlineData("SIHSUS", "RD", 2108, 7, 2107, "2008-07")]
    [InlineData("SIHSUS", "SP", 2108, 7, 2107, "2008-07")]
    [InlineData("SIHSUS", "RJ", 2108, 7, 2107, "2008-07")]
    public void GetPath_MonthBeyondTheYearsOfTheCurrentLayout_IsRefused_WithTheNamesakeInThatLayout(string source, string code, int year, int month, int lastYear, string namesake)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfMonth(year, month);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.SP, period));

        Assert.Contains($"{dataset} file names carry two digits of the year, so the catalog builds them only up to {lastYear}: {period} would get the name of {namesake}.", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(lastYear, 12)));
    }

    // Names of the older folders, read by the older layout of the dataset.
    [Theory]
    [InlineData("SIASUS", "PA", "PAMG0707.dbc", DatasusUf.MG, 2007, 7)]
    [InlineData("SIASUS", "PA", "PAAC9412.DBC", DatasusUf.AC, 1994, 12)]
    [InlineData("SIASUS", "PA", "PAMA0409.dbc", DatasusUf.MA, 2004, 9)]
    [InlineData("SIASUS", "PA", "PAPA9407.dbc", DatasusUf.PA, 1994, 7)]
    [InlineData("SIHSUS", "RD", "RDRR9712.dbc", DatasusUf.RR, 1997, 12)]
    [InlineData("SIHSUS", "RD", "RDAP9201.dbc", DatasusUf.AP, 1992, 1)]
    [InlineData("SIHSUS", "SP", "SPRR9712.dbc", DatasusUf.RR, 1997, 12)]
    [InlineData("SIHSUS", "SP", "SPSP0710.dbc", DatasusUf.SP, 2007, 10)]
    [InlineData("SIHSUS", "RJ", "RJRR0703.dbc", DatasusUf.RR, 2007, 3)]
    [InlineData("SIHSUS", "RJ", "RJRJ0604.dbc", DatasusUf.RJ, 2006, 4)]
    [InlineData("SIM", "DO", "DORRR79.DBC", DatasusUf.RR, 1979, 0)]
    [InlineData("SIM", "DO", "DORRR95.DBC", DatasusUf.RR, 1995, 0)]
    [InlineData("SIM", "DO", "dorsp80.dbc", DatasusUf.SP, 1980, 0)]
    [InlineData("SINASC", "DN", "DNRRR1994.dbc", DatasusUf.RR, 1994, 0)]
    [InlineData("SINASC", "DN", "DNRAP1995.dbc", DatasusUf.AP, 1995, 0)]
    public void TryParseFileName_NameOfAnOlderFolder_IsReadByTheOlderLayout(string source, string code, string fileName, DatasusUf uf, int year, int month)
    {
        var older = DatasusCatalog.Get(source, code).Eras[0];

        Assert.True(older.TryParseFileName(fileName, out var parsedUf, out var period, out var part));

        Assert.Equal(uf, parsedUf);
        Assert.Equal(month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month), period);
        Assert.Null(part);
    }

    // Names of the folders in use now, and other names of the older folders, are not files of the
    // older layout of the dataset.
    [Theory]
    [InlineData("SIASUS", "PA", "PAMG0801.dbc")]       // the first month of the layout in use now
    [InlineData("SIASUS", "PA", "PAMG2407.dbc")]
    [InlineData("SIASUS", "PA", "PAMG2407a.dbc")]
    [InlineData("SIASUS", "PA", "PAMG9406.dbc")]       // the month before the older layout: it would be 2094-06, after its end
    [InlineData("SIASUS", "PA", "PABR0707.dbc")]
    [InlineData("SIHSUS", "RD", "RDSP0801.dbc")]
    [InlineData("SIHSUS", "RD", "RDSP9112.dbc")]       // the month before the older layout
    [InlineData("SIHSUS", "RD", "SPSP0707.dbc")]       // another file type of the same folder
    [InlineData("SIHSUS", "SP", "RDSP0707.dbc")]
    [InlineData("SIHSUS", "RJ", "RDRJ0707.dbc")]
    [InlineData("SIM", "DO", "DORR1996.dbc")]          // the name of the layout in use now
    [InlineData("SIM", "DO", "DORR1995.dbc")]
    [InlineData("SIM", "DO", "DORRR96.dbc")]           // the year after the older layout
    [InlineData("SIM", "DO", "DORRR78.dbc")]
    [InlineData("SIM", "DO", "DORBR95.dbc")]           // the file for the whole country
    [InlineData("SIM", "DO", "DORIG95.DBC")]           // "IG" is not a state
    [InlineData("SINASC", "DN", "DNRR1996.dbc")]
    [InlineData("SINASC", "DN", "DNRRR1996.dbc")]
    [InlineData("SINASC", "DN", "DNRRR1993.dbc")]
    [InlineData("SINASC", "DN", "DNRRR95.dbc")]
    [InlineData("SINASC", "DN", "DNBR1994.dbc")]
    [InlineData("SINASC", "DN", "DNRIG1995.dbc")]
    public void TryParseFileName_NameOutsideTheOlderLayout_IsNotAFileOfIt(string source, string code, string fileName)
    {
        var older = DatasusCatalog.Get(source, code).Eras[0];

        Assert.False(older.TryParseFileName(fileName, out _, out _, out _));
    }

    // A yearly name of an older folder is not a file of the layout in use now: the prefix and
    // the digits of the year are other.
    [Theory]
    [InlineData("SIM", "DO", "DORRR95.DBC")]
    [InlineData("SIM", "DO", "DORRR79.DBC")]
    [InlineData("SINASC", "DN", "DNRRR1995.dbc")]
    [InlineData("SINASC", "DN", "DNRRR1994.dbc")]
    public void TryParseFileName_YearlyNameOfAnOlderFolder_IsNotAFileOfTheCurrentLayout(string source, string code, string fileName)
    {
        Assert.False(DatasusCatalog.Get(source, code).CurrentEra.TryParseFileName(fileName, out _, out _, out _));
    }

    // A monthly name is the same in both layouts, so the layout in use now reads a name of the
    // older folder as a month of its own hundred years, never as the month it is there.
    [Theory]
    [InlineData("SIASUS", "PA", "PAMG0707.dbc", 2007, 2107, 7)]
    [InlineData("SIASUS", "PA", "PAAC9412.DBC", 1994, 2094, 12)]
    [InlineData("SIHSUS", "RD", "RDRR9201.dbc", 1992, 2092, 1)]
    [InlineData("SIHSUS", "SP", "SPRR0707.dbc", 2007, 2107, 7)]
    [InlineData("SIHSUS", "RJ", "RJRR0703.dbc", 2007, 2107, 3)]
    public void TryParseFileName_MonthlyNameOfAnOlderFolder_IsAMonthOfItsOwnLayoutForEachOne(string source, string code, string fileName, int yearInTheOlderLayout, int yearInTheCurrentLayout, int month)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.True(dataset.Eras[0].TryParseFileName(fileName, out _, out var older, out _));
        Assert.True(dataset.CurrentEra.TryParseFileName(fileName, out _, out var current, out _));

        Assert.Equal(DatasusPeriod.OfMonth(yearInTheOlderLayout, month), older);
        Assert.Equal(DatasusPeriod.OfMonth(yearInTheCurrentLayout, month), current);
        Assert.True(dataset.Eras[0].Contains(older));
        Assert.False(dataset.Eras[0].Contains(current));
    }

    [Fact]
    public void Coverage_AndPeriodicity_AreThoseOfTheDataset_WhateverTheLayout()
    {
        Assert.Equal(UfAndNational, DatasusCatalog.Sim.DO.Coverage);
        Assert.Equal(DatasusPeriodicity.Yearly, DatasusCatalog.Sim.DO.Eras[0].Periodicity);
        Assert.Equal(DatasusPeriodicity.Monthly, DatasusCatalog.SiaSus.PA.Eras[0].Periodicity);
        Assert.Equal(2, DatasusCatalog.Sim.DO.Eras[0].YearDigits);
        Assert.Equal(4, DatasusCatalog.Sinasc.DN.Eras[0].YearDigits);
    }
}
