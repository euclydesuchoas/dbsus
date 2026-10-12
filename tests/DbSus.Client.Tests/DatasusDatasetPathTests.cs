using Xunit;

namespace DbSus.Client.Tests;

public class DatasusDatasetPathTests
{
    // Real file names, as the DATASUS server listed them in October 2026: for each dataset, its
    // first, a middle and its last file, plus names in an unusual case. A month of 0 means a year.
    [Theory]
    [InlineData("SIASUS", "PA", DatasusUf.AC, 2008, 1, "/SIASUS/200801_/Dados/PAAC0801.dbc")]
    [InlineData("SIASUS", "PA", DatasusUf.PI, 2016, 12, "/SIASUS/200801_/Dados/PAPI1612.dbc")]
    [InlineData("SIASUS", "PA", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/PATO2607.dbc")]
    [InlineData("SIASUS", "AD", DatasusUf.AC, 2008, 1, "/SIASUS/200801_/Dados/ADAC0801.dbc")]
    [InlineData("SIASUS", "AD", DatasusUf.PB, 2017, 4, "/SIASUS/200801_/Dados/ADPB1704.dbc")]
    [InlineData("SIASUS", "AD", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/ADTO2607.dbc")]
    [InlineData("SIASUS", "AM", DatasusUf.AC, 2008, 1, "/SIASUS/200801_/Dados/AMAC0801.dbc")]
    [InlineData("SIASUS", "AM", DatasusUf.MA, 2017, 4, "/SIASUS/200801_/Dados/AMMA1704.dbc")]
    [InlineData("SIASUS", "AM", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/AMTO2607.dbc")]
    [InlineData("SIASUS", "AQ", DatasusUf.AL, 2008, 1, "/SIASUS/200801_/Dados/AQAL0801.dbc")]
    [InlineData("SIASUS", "AQ", DatasusUf.RJ, 2017, 4, "/SIASUS/200801_/Dados/AQRJ1704.dbc")]
    [InlineData("SIASUS", "AQ", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/AQTO2607.dbc")]
    [InlineData("SIASUS", "AQ", DatasusUf.PA, 2010, 1, "/SIASUS/200801_/Dados/AQPA1001.DBC")]
    [InlineData("SIASUS", "AR", DatasusUf.AL, 2008, 1, "/SIASUS/200801_/Dados/ARAL0801.dbc")]
    [InlineData("SIASUS", "AR", DatasusUf.SE, 2017, 3, "/SIASUS/200801_/Dados/ARSE1703.dbc")]
    [InlineData("SIASUS", "AR", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/ARTO2607.dbc")]
    [InlineData("SIASUS", "AR", DatasusUf.PA, 2010, 1, "/SIASUS/200801_/Dados/ARPA1001.DBC")]
    [InlineData("SIASUS", "PS", DatasusUf.MG, 2012, 11, "/SIASUS/200801_/Dados/PSMG1211.dbc")]
    [InlineData("SIASUS", "PS", DatasusUf.PI, 2019, 10, "/SIASUS/200801_/Dados/PSPI1910.dbc")]
    [InlineData("SIASUS", "PS", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/PSTO2607.dbc")]
    [InlineData("SIASUS", "ATD", DatasusUf.AC, 2014, 8, "/SIASUS/200801_/Dados/ATDAC1408.dbc")]
    [InlineData("SIASUS", "ATD", DatasusUf.AM, 2020, 8, "/SIASUS/200801_/Dados/ATDAM2008.dbc")]
    [InlineData("SIASUS", "ATD", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/ATDTO2607.dbc")]
    [InlineData("SIASUS", "ACF", DatasusUf.AC, 2014, 8, "/SIASUS/200801_/Dados/ACFAC1408.dbc")]
    [InlineData("SIASUS", "ACF", DatasusUf.TO, 2020, 8, "/SIASUS/200801_/Dados/ACFTO2008.dbc")]
    [InlineData("SIASUS", "ACF", DatasusUf.TO, 2026, 7, "/SIASUS/200801_/Dados/ACFTO2607.dbc")]
    [InlineData("SIHSUS", "RD", DatasusUf.AC, 2008, 1, "/SIHSUS/200801_/Dados/RDAC0801.dbc")]
    [InlineData("SIHSUS", "RD", DatasusUf.MT, 2017, 4, "/SIHSUS/200801_/Dados/RDMT1704.dbc")]
    [InlineData("SIHSUS", "RD", DatasusUf.TO, 2026, 7, "/SIHSUS/200801_/Dados/RDTO2607.dbc")]
    [InlineData("SIHSUS", "SP", DatasusUf.AC, 2008, 1, "/SIHSUS/200801_/Dados/SPAC0801.dbc")]
    [InlineData("SIHSUS", "SP", DatasusUf.MT, 2017, 4, "/SIHSUS/200801_/Dados/SPMT1704.dbc")]
    [InlineData("SIHSUS", "SP", DatasusUf.TO, 2026, 7, "/SIHSUS/200801_/Dados/SPTO2607.dbc")]
    [InlineData("SIHSUS", "SP", DatasusUf.PA, 2010, 1, "/SIHSUS/200801_/Dados/sppa1001.dbc")]
    [InlineData("SIHSUS", "RJ", DatasusUf.AC, 2008, 1, "/SIHSUS/200801_/Dados/RJAC0801.dbc")]
    [InlineData("SIHSUS", "RJ", DatasusUf.ES, 2017, 5, "/SIHSUS/200801_/Dados/RJES1705.dbc")]
    [InlineData("SIHSUS", "RJ", DatasusUf.TO, 2026, 7, "/SIHSUS/200801_/Dados/RJTO2607.dbc")]
    [InlineData("SIHSUS", "ER", DatasusUf.AC, 2011, 1, "/SIHSUS/200801_/Dados/ERAC1101.dbc")]
    [InlineData("SIHSUS", "ER", DatasusUf.AM, 2018, 11, "/SIHSUS/200801_/Dados/ERAM1811.dbc")]
    [InlineData("SIHSUS", "ER", DatasusUf.TO, 2026, 7, "/SIHSUS/200801_/Dados/ERTO2607.dbc")]
    [InlineData("CNES", "LT", DatasusUf.AC, 2005, 10, "/CNES/200508_/Dados/LT/LTAC0510.dbc")]
    [InlineData("CNES", "LT", DatasusUf.PA, 2016, 3, "/CNES/200508_/Dados/LT/LTPA1603.dbc")]
    [InlineData("CNES", "LT", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/LT/LTTO2608.dbc")]
    [InlineData("CNES", "EQ", DatasusUf.AC, 2005, 8, "/CNES/200508_/Dados/EQ/EQAC0508.dbc")]
    [InlineData("CNES", "EQ", DatasusUf.PB, 2016, 2, "/CNES/200508_/Dados/EQ/EQPB1602.dbc")]
    [InlineData("CNES", "EQ", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/EQ/EQTO2608.dbc")]
    [InlineData("CNES", "ST", DatasusUf.AC, 2005, 8, "/CNES/200508_/Dados/ST/STAC0508.dbc")]
    [InlineData("CNES", "ST", DatasusUf.PA, 2016, 2, "/CNES/200508_/Dados/ST/STPA1602.dbc")]
    [InlineData("CNES", "ST", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/ST/STTO2608.dbc")]
    [InlineData("CNES", "PF", DatasusUf.AC, 2005, 8, "/CNES/200508_/Dados/PF/PFAC0508.dbc")]
    [InlineData("CNES", "PF", DatasusUf.PA, 2016, 2, "/CNES/200508_/Dados/PF/PFPA1602.dbc")]
    [InlineData("CNES", "PF", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/PF/PFTO2608.dbc")]
    [InlineData("CNES", "SR", DatasusUf.AC, 2005, 8, "/CNES/200508_/Dados/SR/SRAC0508.dbc")]
    [InlineData("CNES", "SR", DatasusUf.PA, 2016, 2, "/CNES/200508_/Dados/SR/SRPA1602.dbc")]
    [InlineData("CNES", "SR", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/SR/SRTO2608.dbc")]
    [InlineData("CNES", "HB", DatasusUf.AC, 2007, 3, "/CNES/200508_/Dados/HB/HBAC0703.dbc")]
    [InlineData("CNES", "HB", DatasusUf.AC, 2016, 12, "/CNES/200508_/Dados/HB/HBAC1612.dbc")]
    [InlineData("CNES", "HB", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/HB/HBTO2608.dbc")]
    [InlineData("CNES", "DC", DatasusUf.AC, 2005, 8, "/CNES/200508_/Dados/DC/DCAC0508.dbc")]
    [InlineData("CNES", "DC", DatasusUf.PA, 2016, 2, "/CNES/200508_/Dados/DC/DCPA1602.dbc")]
    [InlineData("CNES", "DC", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/DC/DCTO2608.dbc")]
    [InlineData("CNES", "EP", DatasusUf.AC, 2007, 4, "/CNES/200508_/Dados/EP/EPAC0704.dbc")]
    [InlineData("CNES", "EP", DatasusUf.PA, 2016, 12, "/CNES/200508_/Dados/EP/EPPA1612.dbc")]
    [InlineData("CNES", "EP", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/EP/EPTO2608.dbc")]
    [InlineData("CNES", "EF", DatasusUf.AC, 2007, 3, "/CNES/200508_/Dados/EF/EFAC0703.dbc")]
    [InlineData("CNES", "EF", DatasusUf.TO, 2016, 11, "/CNES/200508_/Dados/EF/EFTO1611.dbc")]
    [InlineData("CNES", "EF", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/EF/EFTO2608.dbc")]
    [InlineData("CNES", "GM", DatasusUf.BA, 2007, 3, "/CNES/200508_/Dados/GM/GMBA0703.dbc")]
    [InlineData("CNES", "GM", DatasusUf.MS, 2017, 2, "/CNES/200508_/Dados/GM/GMMS1702.dbc")]
    [InlineData("CNES", "GM", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/GM/GMTO2608.dbc")]
    [InlineData("CNES", "IN", DatasusUf.AL, 2007, 10, "/CNES/200508_/Dados/IN/INAL0710.dbc")]
    [InlineData("CNES", "IN", DatasusUf.ES, 2017, 5, "/CNES/200508_/Dados/IN/INES1705.dbc")]
    [InlineData("CNES", "IN", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/IN/INTO2608.dbc")]
    [InlineData("CNES", "RC", DatasusUf.AC, 2007, 3, "/CNES/200508_/Dados/RC/RCAC0703.dbc")]
    [InlineData("CNES", "RC", DatasusUf.AP, 2017, 1, "/CNES/200508_/Dados/RC/RCAP1701.dbc")]
    [InlineData("CNES", "RC", DatasusUf.TO, 2026, 8, "/CNES/200508_/Dados/RC/RCTO2608.dbc")]
    [InlineData("SIM", "DO", DatasusUf.AC, 1996, 0, "/SIM/CID10/DORES/DOAC1996.dbc")]
    [InlineData("SIM", "DO", DatasusUf.PA, 2010, 0, "/SIM/CID10/DORES/DOPA2010.DBC")]
    [InlineData("SIM", "DO", DatasusUf.TO, 2024, 0, "/SIM/CID10/DORES/DOTO2024.dbc")]
    [InlineData("SIM", "DO", DatasusUf.TO, 2012, 0, "/SIM/CID10/DORES/DOTO2012.DBC")]
    [InlineData("SINASC", "DN", DatasusUf.AC, 1996, 0, "/SINASC/1996_/Dados/DNRES/DNAC1996.DBC")]
    [InlineData("SINASC", "DN", DatasusUf.PA, 2010, 0, "/SINASC/1996_/Dados/DNRES/DNPA2010.DBC")]
    [InlineData("SINASC", "DN", DatasusUf.TO, 2024, 0, "/SINASC/1996_/Dados/DNRES/DNTO2024.dbc")]
    [InlineData("SINASC", "DN", DatasusUf.SP, 2018, 0, "/SINASC/1996_/Dados/DNRES/DNSP2018.DBC")]
    public void GetPath_ReproducesARealPath_IgnoringCase(string source, string code, DatasusUf uf, int year, int month, string realPath)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month);

        var path = dataset.GetPath(uf, period);

        Assert.Equal(realPath, path, ignoreCase: true);
        Assert.Equal(realPath.Remove(realPath.LastIndexOf('/')), path.Remove(path.LastIndexOf('/')));
        Assert.Equal(path, dataset.GetCandidatePaths(uf, period)[0]);

        // And back: the real name reads as the same state and period.
        Assert.True(dataset.CurrentEra.TryParseFileName(realPath.Substring(realPath.LastIndexOf('/') + 1), out var parsedUf, out var parsedPeriod, out var part));
        Assert.Equal(uf, parsedUf);
        Assert.Equal(period, parsedPeriod);
        Assert.Null(part);
    }

    [Fact]
    public void GetPath_BuildsUpperCaseNamesWithALowerCaseExtension()
    {
        Assert.Equal("/SIHSUS/200801_/Dados/RDSP2407.dbc", DatasusCatalog.SihSus.RD.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2024, 7)));
        Assert.Equal("/CNES/200508_/Dados/LT/LTRR0610.dbc", DatasusCatalog.Cnes.LT.GetPath(DatasusUf.RR, DatasusPeriod.OfMonth(2006, 10)));
        Assert.Equal("/SIM/CID10/DORES/DORR2011.dbc", DatasusCatalog.Sim.DO.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(2011)));
    }

    [Fact]
    public void GetPath_SplitTable_NamesTheFileWithoutAPartLetter()
    {
        // On the server: PAMG2407a.dbc and PAMG2407b.dbc.
        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407.dbc", DatasusCatalog.SiaSus.PA.GetPath(DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7)));
    }

    [Fact]
    public void GetCandidatePaths_MonthlySources_HaveASingleCandidate()
    {
        foreach (var dataset in DatasusCatalog.Datasets.Where(dataset => dataset.Periodicity == DatasusPeriodicity.Monthly))
        {
            var period = DatasusPeriod.OfMonth(2024, 7);

            Assert.Equal([dataset.GetPath(DatasusUf.SP, period)], dataset.GetCandidatePaths(DatasusUf.SP, period));
        }
    }

    // Real preliminary files, as listed in October 2026.
    [Theory]
    [InlineData("SIM", "DO", "/SIM/CID10/DORES/DORR2025.dbc", "/SIM/PRELIM/DORES/DORR2025.dbc")]
    [InlineData("SINASC", "DN", "/SINASC/1996_/Dados/DNRES/DNRR2025.dbc", "/SINASC/PRELIM/DNRES/DNRR2025.dbc")]
    public void GetCandidatePaths_YearlySources_GiveTheFinalPathThenThePreliminaryOne(string source, string code, string finalPath, string preliminaryPath)
    {
        var dataset = DatasusCatalog.Get(source, code);

        var candidates = dataset.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(2025));

        Assert.Equal([finalPath, preliminaryPath], candidates);
        Assert.Equal(finalPath, dataset.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(2025)));
    }

    [Fact]
    public void GetPath_NeverChecksAvailability()
    {
        // A month long after the last one published, a state the file type never had (SIA-AR has
        // no file of Roraima), and a period before the file type started (CNES-LT began in 2005-10).
        Assert.Equal("/SIASUS/200801_/Dados/PASP9912.dbc", DatasusCatalog.SiaSus.PA.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2099, 12)));
        Assert.Equal("/SIASUS/200801_/Dados/ARRR2401.dbc", DatasusCatalog.SiaSus.AR.GetPath(DatasusUf.RR, DatasusPeriod.OfMonth(2024, 1)));
        Assert.Equal("/CNES/200508_/Dados/LT/LTSP0508.dbc", DatasusCatalog.Cnes.LT.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2005, 8)));
        Assert.Equal("/SIM/CID10/DORES/DOSP2090.dbc", DatasusCatalog.Sim.DO.GetPath(DatasusUf.SP, DatasusPeriod.OfYear(2090)));
    }

    [Theory]
    [InlineData((DatasusUf)0)]
    [InlineData((DatasusUf)34)]
    [InlineData((DatasusUf)(-1))]
    public void GetPath_ValueThatIsNotAState_Throws(DatasusUf uf)
    {
        var period = DatasusPeriod.OfMonth(2024, 7);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.SihSus.RD.GetPath(uf, period));

        Assert.Equal(nameof(uf), exception.ParamName);
        Assert.Contains("default(DatasusUf)", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.SihSus.RD.GetCandidatePaths(uf, period));
    }

    [Fact]
    public void GetPath_PeriodNotSet_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.SihSus.RD.GetPath(DatasusUf.SP, default));

        Assert.Equal("period", exception.ParamName);
        Assert.Contains("not set", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DatasusCatalog.Sim.DO.GetCandidatePaths(DatasusUf.SP, default));
    }

    [Fact]
    public void GetPath_MonthlyDatasetWithoutAMonth_ThrowsSayingAMonthIsNeeded()
    {
        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.SihSus.RD.GetPath(DatasusUf.SP, DatasusPeriod.OfYear(2024)));

        Assert.IsNotType<ArgumentOutOfRangeException>(exception);
        Assert.Equal("period", exception.ParamName);
        Assert.Contains("SIHSUS-RD is published monthly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DatasusPeriod.OfMonth(2024, 1)", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetPath_YearlyDatasetWithAMonth_ThrowsSayingAYearIsNeeded()
    {
        var exception = Assert.Throws<ArgumentException>(() => DatasusCatalog.Sim.DO.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2020, 5)));

        Assert.IsNotType<ArgumentOutOfRangeException>(exception);
        Assert.Contains("SIM-DO is published yearly", exception.Message, StringComparison.Ordinal);
        Assert.Contains("DatasusPeriod.OfYear(2020)", exception.Message, StringComparison.Ordinal);
    }

    // The period just before each folder layout in use now, and its first period.
    [Theory]
    [InlineData("SIASUS", "PA", 2007, 12, 2008, 1, "2008-01")]
    [InlineData("SIHSUS", "RD", 2007, 12, 2008, 1, "2008-01")]
    [InlineData("CNES", "ST", 2005, 7, 2005, 8, "2005-08")]
    [InlineData("SIM", "DO", 1995, 0, 1996, 0, "1996")]
    [InlineData("SINASC", "DN", 1995, 0, 1996, 0, "1996")]
    public void GetPath_PeriodBeforeTheCurrentLayout_ThrowsPointingToTheStringPath(string source, string code, int year, int month, int firstYear, int firstMonth, string firstPeriod)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var before = month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month);
        var first = firstMonth == 0 ? DatasusPeriod.OfYear(firstYear) : DatasusPeriod.OfMonth(firstYear, firstMonth);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.SP, before));

        Assert.Equal("period", exception.ParamName);
        Assert.Equal(before, exception.ActualValue);
        Assert.Contains($"{dataset} files before {firstPeriod} are in an older folder layout", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Use the path as a string instead", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docs/datasus-paths.md", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetCandidatePaths(DatasusUf.SP, before));
        Assert.Equal(first, dataset.FirstPeriod);
        Assert.NotNull(dataset.GetPath(DatasusUf.SP, first));
    }

    // A monthly file name carries two digits of the year, so it can name the hundred years that
    // start with the folder layout and no other: a later month would get the name of the month one
    // hundred years before it.
    [Theory]
    [InlineData("SIHSUS", "RD", 2108, 7, 2107, "2008-07")]
    [InlineData("SIHSUS", "RD", 2208, 7, 2107, "2008-07")]
    [InlineData("SIASUS", "PA", 2108, 1, 2107, "2008-01")]
    [InlineData("CNES", "LT", 2105, 8, 2104, "2005-08")]
    [InlineData("CIHA", "CIHA", 2111, 1, 2110, "2011-01")]
    public void GetPath_MonthBeyondTheYearsTwoDigitsCanName_ThrowsSayingWhoseNameItWouldGet(string source, string code, int year, int month, int lastYear, string namesake)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = DatasusPeriod.OfMonth(year, month);

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.SP, period));

        Assert.Equal("period", exception.ParamName);
        Assert.Equal(period, exception.ActualValue);
        Assert.Contains($"{dataset} file names carry two digits of the year, so the catalog builds them only up to {lastYear}: {period} would get the name of {namesake}.", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetCandidatePaths(DatasusUf.SP, period));
    }

    [Fact]
    public void GetPath_LastYearTwoDigitsCanName_IsBuilt_AndItsNameReadsBackAsItself()
    {
        var dataset = DatasusCatalog.SihSus.RD;
        var last = DatasusPeriod.OfMonth(2107, 12);

        Assert.Equal("/SIHSUS/200801_/Dados/RDSP0712.dbc", dataset.GetPath(DatasusUf.SP, last));
        Assert.True(dataset.CurrentEra.TryParseFileName("RDSP0712.dbc", out _, out var readBack, out _));
        Assert.Equal(last, readBack);
    }

    [Fact]
    public void GetPath_YearlyDataset_HasNoSuchLimit_BecauseItsNamesCarryFourDigits()
    {
        var dataset = DatasusCatalog.Sim.DO;

        Assert.Equal("/SIM/CID10/DORES/DOSP2096.dbc", dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfYear(2096)));
        Assert.Equal("/SIM/CID10/DORES/DOSP9999.dbc", dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfYear(9999)));
    }
}
