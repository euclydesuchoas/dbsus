using Xunit;

namespace DbSus.Client.Tests;

// The forms of an era that no file type of the catalog uses yet: an era that has ended, a yearly
// name with two digits of the year, a file for the whole country with BR in place of the state,
// and one with no area in the name. The eras here are built by the tests, with folders and names
// that the listings of October 2026 showed, except two that are made up and say so.
public class DatasusEraTests
{
    private static readonly DatasusEra SimUpTo1995 = new(DatasusPeriod.OfYear(1979), DatasusPeriod.OfYear(1995), "/SIM/CID9/DORES", null, "DOR", "DORBR", 2);
    private static readonly DatasusEra SimFrom1996 = new(DatasusPeriod.OfYear(1996), null, "/SIM/CID10/DORES", "/SIM/PRELIM/DORES", "DO", "DOBR", 4);
    private static readonly DatasusEra SiaUpTo2007 = new(DatasusPeriod.OfMonth(1994, 7), DatasusPeriod.OfMonth(2007, 12), "/SIASUS/199407_200712/Dados", null, "PA", null, 2);
    private static readonly DatasusEra Pce = new(DatasusPeriod.OfYear(1995), null, "/PCE/Dados", null, "PCE", "PCEBR", 2);
    private static readonly DatasusEra SimExternalCauses = new(DatasusPeriod.OfYear(1996), null, "/SIM/CID10/DOFET", "/SIM/PRELIM/DOFET", null, "DOEXT", 2);

    // Two eras made up for the tests, in a folder that does not exist: the name of the file of one
    // starts with the name of the file of the other.
    private static readonly DatasusEra MadeUpFrom1996 = new(DatasusPeriod.OfYear(1996), null, "/MADEUP/Dados", null, null, "SYN", 2);
    private static readonly DatasusEra MadeUpFrom2013 = new(DatasusPeriod.OfYear(2013), null, "/MADEUP/Dados", null, null, "SYNX", 2);

    private static readonly DatasusEra SihHospitals = new(DatasusPeriod.OfMonth(2008, 1), null, "/SIHSUS/200801_/Dados", null, null, "CHBR", 2);

    [Fact]
    public void EraThatHasEnded_ContainsItsLastPeriodAndNothingAfter()
    {
        Assert.Equal(DatasusPeriod.OfYear(1995), SimUpTo1995.LastPeriod);
        Assert.True(SimUpTo1995.Contains(DatasusPeriod.OfYear(1979)));
        Assert.True(SimUpTo1995.Contains(DatasusPeriod.OfYear(1995)));
        Assert.False(SimUpTo1995.Contains(DatasusPeriod.OfYear(1996)));
        Assert.False(SimUpTo1995.Contains(DatasusPeriod.OfYear(1978)));

        Assert.True(SiaUpTo2007.Contains(DatasusPeriod.OfMonth(2007, 12)));
        Assert.False(SiaUpTo2007.Contains(DatasusPeriod.OfMonth(2008, 1)));
        Assert.False(SiaUpTo2007.Contains(DatasusPeriod.OfMonth(1994, 6)));
    }

    [Theory]
    [InlineData("DORRR95.DBC", true, 1995)]
    [InlineData("dorsp79.dbc", true, 1979)]
    [InlineData("DORRR96.dbc", false, 0)]      // the year after the era ends
    [InlineData("DORRR78.dbc", false, 0)]      // within the hundred years of the era it would be 2078, after its end
    [InlineData("DORR1995.dbc", false, 0)]     // the name of the layout that came after
    [InlineData("DORBR95.dbc", false, 0)]      // the file for the whole country is not the file of a state
    public void EraThatHasEnded_ReadsOnlyTheNamesOfItsOwnYears(string fileName, bool recognized, int year)
    {
        Assert.Equal(recognized, SimUpTo1995.TryParseFileName(fileName, out _, out var period, out var part));

        if (recognized)
        {
            Assert.Equal(DatasusPeriod.OfYear(year), period);
            Assert.Null(part);
        }
    }

    [Theory]
    [InlineData("PASP0712.dbc", true, 2007, 12)]
    [InlineData("PASP9407.dbc", true, 1994, 7)]
    [InlineData("PASP0801.dbc", false, 0, 0)]  // the first month of the layout that came after
    [InlineData("PASP9406.dbc", false, 0, 0)]  // the month before the era: it would be 2094-06, after its end
    public void MonthlyEraThatHasEnded_ReadsOnlyTheNamesOfItsOwnMonths(string fileName, bool recognized, int year, int month)
    {
        Assert.Equal(recognized, SiaUpTo2007.TryParseFileName(fileName, out var uf, out var period, out _));

        if (recognized)
        {
            Assert.Equal(DatasusUf.SP, uf);
            Assert.Equal(DatasusPeriod.OfMonth(year, month), period);
        }
    }

    [Fact]
    public void YearlyNameWithTwoDigits_IsBuiltAndReadWithinTheHundredYearsOfTheEra()
    {
        Assert.Equal(2, Pce.YearDigits);
        Assert.Equal("PCE{UF}{YY}.dbc", Pce.FileNamePattern);
        Assert.Equal(2094, Pce.LastNameableYear);
        Assert.Equal("PCEPE99.dbc", Pce.FileName("PE", DatasusPeriod.OfYear(1999)));
        Assert.Equal("PCEMG26.dbc", Pce.FileName("MG", DatasusPeriod.OfYear(2026)));

        Assert.True(Pce.TryParseFileName("PCEPE99.dbc", out var uf, out var period, out var part));
        Assert.Equal(DatasusUf.PE, uf);
        Assert.Equal(DatasusPeriod.OfYear(1999), period);
        Assert.Null(part);
        Assert.True(Pce.TryParseFileName("pcemg26.DBC", out _, out period, out _));
        Assert.Equal(DatasusPeriod.OfYear(2026), period);
        Assert.True(Pce.TryParseFileName("PCEPE94.dbc", out _, out period, out _));
        Assert.Equal(DatasusPeriod.OfYear(2094), period);

        Assert.False(Pce.TryParseFileName("PCEPE1999.dbc", out _, out _, out _));
        Assert.False(Pce.TryParseFileName("PCEPE9.dbc", out _, out _, out _));
        Assert.False(Pce.TryParseFileName("PCEBR99.dbc", out _, out _, out _));
        Assert.False(Pce.TryParseFileName("PCEXX99.dbc", out _, out _, out _));
    }

    [Fact]
    public void YearlyNameWithFourDigits_HasNoLastNameableYear()
    {
        Assert.Equal(4, SimFrom1996.YearDigits);
        Assert.Null(SimFrom1996.LastNameableYear);
        Assert.Equal("DO{UF}{YYYY}.dbc", SimFrom1996.FileNamePattern);
        Assert.Equal("DOSP2096.dbc", SimFrom1996.FileName("SP", DatasusPeriod.OfYear(2096)));
    }

    [Fact]
    public void FileForTheWholeCountry_WithBrInPlaceOfTheState_HasItsOwnNameAndIsNotTheFileOfAState()
    {
        Assert.Equal("DOBR{YYYY}.dbc", SimFrom1996.NationalFileNamePattern);
        Assert.Equal("DOBR2011.dbc", SimFrom1996.NationalFileName(DatasusPeriod.OfYear(2011)));
        Assert.True(SimFrom1996.TryParseNationalFileName("dobr2011.DBC", out var period));
        Assert.Equal(DatasusPeriod.OfYear(2011), period);
        Assert.False(SimFrom1996.TryParseNationalFileName("DORR2011.dbc", out _));
        Assert.False(SimFrom1996.TryParseNationalFileName("DOBR1995.dbc", out _));
        Assert.False(SimFrom1996.TryParseNationalFileName("DOBR11.dbc", out _));
        Assert.False(SimFrom1996.TryParseFileName("DOBR2011.dbc", out _, out _, out _));

        Assert.Equal("DORBR{YY}.dbc", SimUpTo1995.NationalFileNamePattern);
        Assert.Equal("DORBR79.dbc", SimUpTo1995.NationalFileName(DatasusPeriod.OfYear(1979)));
        Assert.True(SimUpTo1995.TryParseNationalFileName("DORBR95.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(1995), period);
        Assert.False(SimUpTo1995.TryParseNationalFileName("DORBR96.dbc", out _));

        Assert.Equal("PCEBR{YY}.dbc", Pce.NationalFileNamePattern);
        Assert.Equal("PCEBR26.dbc", Pce.NationalFileName(DatasusPeriod.OfYear(2026)));
    }

    [Fact]
    public void FileForTheWholeCountry_WithNoAreaInTheName_IsTheOnlyFileOfTheEra()
    {
        Assert.Null(SimExternalCauses.StatePrefix);
        Assert.Null(SimExternalCauses.StateFileNamePattern);
        Assert.Equal("DOEXT{YY}.dbc", SimExternalCauses.NationalFileNamePattern);
        Assert.Equal("DOEXT{YY}.dbc", SimExternalCauses.FileNamePattern);
        Assert.Equal("DOEXT13.dbc", SimExternalCauses.NationalFileName(DatasusPeriod.OfYear(2013)));
        Assert.Equal(2095, SimExternalCauses.LastNameableYear);

        Assert.True(SimExternalCauses.TryParseNationalFileName("DOEXT96.DBC", out var period));
        Assert.Equal(DatasusPeriod.OfYear(1996), period);
        Assert.True(SimExternalCauses.TryParseNationalFileName("doext25.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(2025), period);

        // No file of a state: nothing is read as one, and none can be built.
        Assert.False(SimExternalCauses.TryParseFileName("DOEXT13.dbc", out _, out _, out _));
        Assert.False(SimExternalCauses.TryParseFileName("DOEXTSP13.dbc", out _, out _, out _));
        Assert.Throws<InvalidOperationException>(() => SimExternalCauses.FileName("SP", DatasusPeriod.OfYear(2013)));
    }

    [Fact]
    public void FileForTheWholeCountry_IsNotMistakenForOneWhoseNameStartsWithIts()
    {
        // The two made-up eras share a folder.
        Assert.False(MadeUpFrom1996.TryParseNationalFileName("SYNX13.dbc", out _));
        Assert.False(MadeUpFrom2013.TryParseNationalFileName("SYN13.dbc", out _));
        Assert.True(MadeUpFrom1996.TryParseNationalFileName("SYN13.dbc", out var period));
        Assert.Equal(DatasusPeriod.OfYear(2013), period);
        Assert.True(MadeUpFrom2013.TryParseNationalFileName("SYNX13.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(2013), period);
    }

    [Fact]
    public void TwoDigitYear_BeforeTheFirstYearOfAnEraThatHasNotEnded_NamesTheLastOfItsHundredYears()
    {
        // The made-up era starts in 2013 and has not ended: 12 names 2112, as in a monthly name.
        Assert.True(MadeUpFrom2013.TryParseNationalFileName("SYNX12.dbc", out var period));
        Assert.Equal(DatasusPeriod.OfYear(2112), period);
        Assert.Equal(2112, MadeUpFrom2013.LastNameableYear);

        // The one that starts in 1996 reads the same two digits as 2012.
        Assert.True(MadeUpFrom1996.TryParseNationalFileName("SYN12.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(2012), period);
        Assert.True(MadeUpFrom1996.TryParseNationalFileName("SYN96.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(1996), period);
        Assert.Equal(2095, MadeUpFrom1996.LastNameableYear);
    }

    [Fact]
    public void MonthlyFileForTheWholeCountry_IsBuiltAndRead()
    {
        Assert.Equal("CHBR{YY}{MM}.dbc", SihHospitals.NationalFileNamePattern);
        Assert.Equal("CHBR1901.dbc", SihHospitals.NationalFileName(DatasusPeriod.OfMonth(2019, 1)));
        Assert.True(SihHospitals.TryParseNationalFileName("CHBR1901.dbc", out var period));
        Assert.Equal(DatasusPeriod.OfMonth(2019, 1), period);
        Assert.False(SihHospitals.TryParseNationalFileName("CHBR1913.dbc", out _));
        Assert.False(SihHospitals.TryParseNationalFileName("CHBR190.dbc", out _));
        Assert.False(SihHospitals.TryParseNationalFileName("CHSP1901.dbc", out _));
        Assert.False(SihHospitals.TryParseNationalFileName("CHBR1901a.dbc", out _));
    }

    [Fact]
    public void EraWithoutAFileForTheWholeCountry_BuildsAndReadsNone()
    {
        Assert.Null(SiaUpTo2007.NationalStem);
        Assert.Null(SiaUpTo2007.NationalFileNamePattern);
        Assert.False(SiaUpTo2007.TryParseNationalFileName("PABR0712.dbc", out _));
        Assert.Throws<InvalidOperationException>(() => SiaUpTo2007.NationalFileName(DatasusPeriod.OfMonth(2007, 12)));
    }

    [Fact]
    public void DatasetWithTwoEras_BuildsThePathOfEachPeriodInItsOwnEra()
    {
        var dataset = new DatasusDataset("SIM", "DO", "Declarações de Óbito", DatasusCoverage.Uf | DatasusCoverage.National, SimUpTo1995, SimFrom1996);

        Assert.Equal("/SIM/CID9/DORES/DORRR95.dbc", dataset.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(1995)));
        Assert.Equal("/SIM/CID10/DORES/DORR1996.dbc", dataset.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(1996)));
        Assert.Equal(["/SIM/CID9/DORES/DORRR79.dbc"], dataset.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(1979)));
        Assert.Equal(["/SIM/CID10/DORES/DORR2025.dbc", "/SIM/PRELIM/DORES/DORR2025.dbc"], dataset.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(2025)));
        Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.RR, DatasusPeriod.OfYear(1978)));
    }

    [Fact]
    public void YearlyDatasetWithTwoDigits_RefusesAYearThatWouldGetTheNameOfAnEarlierOne()
    {
        var dataset = new DatasusDataset("PCE", "PCE", "Programa de Controle da Esquistossomose", DatasusCoverage.Uf | DatasusCoverage.National, Pce);
        var period = DatasusPeriod.OfYear(2095);

        Assert.Equal("/PCE/Dados/PCEPE94.dbc", dataset.GetPath(DatasusUf.PE, DatasusPeriod.OfYear(2094)));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetPath(DatasusUf.PE, period));

        Assert.Equal("period", exception.ParamName);
        Assert.Equal(period, exception.ActualValue);
        Assert.Contains("PCE-PCE file names carry two digits of the year, so the catalog builds them only up to 2094: 2095 would get the name of 1995.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_RefusesAnEraThatCouldNotNameAFile()
    {
        var year = DatasusPeriod.OfYear(1996);
        var month = DatasusPeriod.OfMonth(2008, 1);

        Assert.Throws<ArgumentException>(() => new DatasusEra(year, null, "/X", null, null, null, 4));                          // no name at all
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasusEra(year, null, "/X", null, "X", null, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasusEra(month, null, "/X", null, "X", null, 4));               // a monthly name carries two digits
        Assert.Throws<ArgumentException>(() => new DatasusEra(year, DatasusPeriod.OfYear(1995), "/X", null, "X", null, 4));    // ends before it starts
        Assert.Throws<ArgumentException>(() => new DatasusEra(year, month, "/X", null, "X", null, 4));                         // a year and a month
    }
}
