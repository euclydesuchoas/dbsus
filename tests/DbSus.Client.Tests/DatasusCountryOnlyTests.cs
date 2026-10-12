using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

// The eleven file types that have one file for the whole country and none by state: five of SIM,
// SINASC-DNEX, SIHSUS CH, CM and MT, ESUSNOTIFICA-DCCR and PO-PO. The file names are real ones,
// as the DATASUS server listed them in October 2026.
public sealed class DatasusCountryOnlyTests : IDisposable
{
    private const string SimNow = "/SIM/CID10/DOFET";
    private const string SimPreliminary = "/SIM/PRELIM/DOFET";
    private const string SimOlder = "/SIM/CID9/DOFET";
    private const string Sih = "/SIHSUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    // The path of one real file of each, and of each older folder, with the first period of the
    // file type: the start of its oldest folder, or the first file seen when the folder has no date.
    [Theory]
    [InlineData("SIM", "DOEXT", 1996, 0, "/SIM/CID10/DOFET/DOEXT96.dbc", "1979")]
    [InlineData("SIM", "DOEXT", 1979, 0, "/SIM/CID9/DOFET/DOEXT79.dbc", "1979")]
    [InlineData("SIM", "DOFET", 1997, 0, "/SIM/CID10/DOFET/DOFET97.dbc", "1979")]
    [InlineData("SIM", "DOFET", 1990, 0, "/SIM/CID9/DOFET/DOFET90.dbc", "1979")]
    [InlineData("SIM", "DOINF", 1997, 0, "/SIM/CID10/DOFET/DOINF97.dbc", "1979")]
    [InlineData("SIM", "DOINF", 1991, 0, "/SIM/CID9/DOFET/DOINF91.dbc", "1979")]
    [InlineData("SIM", "DOMAT", 1996, 0, "/SIM/CID10/DOFET/DOMAT96.dbc", "1996")]
    [InlineData("SIM", "DOREXT", 2020, 0, "/SIM/CID10/DOFET/DOREXT20.dbc", "1996")]
    [InlineData("SINASC", "DNEX", 2020, 0, "/SINASC/1996_/Dados/DNRES/DNEX2020.dbc", "1996")]
    [InlineData("SIHSUS", "CH", 2020, 1, "/SIHSUS/200801_/Dados/CHBR2001.dbc", "2008-01")]
    [InlineData("SIHSUS", "CM", 2020, 1, "/SIHSUS/200801_/Dados/CMBR2001.dbc", "2008-01")]
    [InlineData("SIHSUS", "MT", 1996, 12, "/SIHSUS/Arquivos_MTBR/MTBR9612.dbc", "1995-01")]
    [InlineData("ESUSNOTIFICA", "DCCR", 2023, 0, "/ESUSNOTIFICA/DADOS/FINAIS/DCCRBR23.dbc", "2023")]
    [InlineData("PO", "PO", 2013, 0, "/painel_oncologia/Dados/POBR2013.dbc", "2013")]
    public void GetNationalPath_BuildsThePathOfARealFile_AndItsNameIsReadBack(string source, string code, int year, int month, string expected, string firstPeriod)
    {
        var dataset = DatasusCatalog.Get(source, code);
        var period = month == 0 ? DatasusPeriod.OfYear(year) : DatasusPeriod.OfMonth(year, month);

        Assert.Equal(expected, dataset.GetNationalPath(period));
        Assert.Equal(expected, dataset.GetNationalCandidatePaths(period)[0]);
        Assert.Equal(firstPeriod, dataset.FirstPeriod.ToString());
        Assert.Equal(DatasusCoverage.National, dataset.Coverage);

        var era = dataset.EraFor(period, nameof(period));
        Assert.True(era.TryParseNationalFileName(expected.Substring(expected.LastIndexOf('/') + 1), out var readBack));
        Assert.Equal(period, readBack);
        Assert.Null(era.StatePrefix);
    }

    [Fact]
    public void GetNationalCandidatePaths_ByFolderLayout()
    {
        // The older folder of SIM has no folder of preliminary data; the one in use now has.
        Assert.Equal(["/SIM/CID9/DOFET/DOFET95.dbc"], DatasusCatalog.Sim.DOFET.GetNationalCandidatePaths(DatasusPeriod.OfYear(1995)));
        Assert.Equal(["/SIM/CID10/DOFET/DOFET96.dbc", "/SIM/PRELIM/DOFET/DOFET96.dbc"], DatasusCatalog.Sim.DOFET.GetNationalCandidatePaths(DatasusPeriod.OfYear(1996)));
        Assert.Equal(["/SIM/CID10/DOFET/DOEXT25.dbc", "/SIM/PRELIM/DOFET/DOEXT25.dbc"], DatasusCatalog.Sim.DOEXT.GetNationalCandidatePaths(DatasusPeriod.OfYear(2025)));
        Assert.Equal(["/SIM/CID10/DOFET/DOMAT26.dbc", "/SIM/PRELIM/DOFET/DOMAT26.dbc"], DatasusCatalog.Sim.DOMAT.GetNationalCandidatePaths(DatasusPeriod.OfYear(2026)));
        Assert.Equal(["/SIM/CID10/DOFET/DOREXT13.dbc", "/SIM/PRELIM/DOFET/DOREXT13.dbc"], DatasusCatalog.Sim.DOREXT.GetNationalCandidatePaths(DatasusPeriod.OfYear(2013)));
        Assert.Equal(
            ["/SINASC/1996_/Dados/DNRES/DNEX2025.dbc", "/SINASC/PRELIM/DNRES/DNEX2025.dbc"],
            DatasusCatalog.Sinasc.DNEX.GetNationalCandidatePaths(DatasusPeriod.OfYear(2025)));
        Assert.Equal(
            ["/ESUSNOTIFICA/DADOS/FINAIS/DCCRBR24.dbc", "/ESUSNOTIFICA/DADOS/PRELIM/DCCRBR24.dbc"],
            DatasusCatalog.EsusNotifica.DCCR.GetNationalCandidatePaths(DatasusPeriod.OfYear(2024)));
        Assert.Equal(["/painel_oncologia/Dados/POBR2026.dbc"], DatasusCatalog.Po.PO.GetNationalCandidatePaths(DatasusPeriod.OfYear(2026)));
        Assert.Equal(["/SIHSUS/200801_/Dados/CHBR2603.dbc"], DatasusCatalog.SihSus.CH.GetNationalCandidatePaths(DatasusPeriod.OfMonth(2026, 3)));
        Assert.Equal(["/SIHSUS/Arquivos_MTBR/MTBR9501.dbc"], DatasusCatalog.SihSus.MT.GetNationalCandidatePaths(DatasusPeriod.OfMonth(1995, 1)));
    }

    // Only DOEXT, DOFET and DOINF have the older folder; the others start with the folder in use now.
    [Fact]
    public void Eras_AreTheFoldersOfEachFileType()
    {
        foreach (var dataset in new[] { DatasusCatalog.Sim.DOEXT, DatasusCatalog.Sim.DOFET, DatasusCatalog.Sim.DOINF })
        {
            Assert.Equal(2, dataset.Eras.Count);
            Assert.Equal((SimOlder, "1979", "1995"), (dataset.Eras[0].Directory, dataset.Eras[0].FirstPeriod.ToString(), dataset.Eras[0].LastPeriod.ToString()));
            Assert.Null(dataset.Eras[0].PreliminaryDirectory);
            Assert.Equal((SimNow, SimPreliminary, "1996"), (dataset.CurrentEra.Directory, dataset.CurrentEra.PreliminaryDirectory, dataset.CurrentEra.FirstPeriod.ToString()));
            Assert.Equal(dataset.Eras[0].NationalFileNamePattern, dataset.CurrentEra.NationalFileNamePattern);
        }

        foreach (var dataset in new[] { DatasusCatalog.Sim.DOMAT, DatasusCatalog.Sim.DOREXT, DatasusCatalog.Sinasc.DNEX, DatasusCatalog.SihSus.CH, DatasusCatalog.SihSus.CM, DatasusCatalog.SihSus.MT, DatasusCatalog.EsusNotifica.DCCR, DatasusCatalog.Po.PO })
        {
            Assert.Null(Assert.Single(dataset.Eras).LastPeriod);
        }

        var beforeMaternal = Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.Sim.DOMAT.GetNationalPath(DatasusPeriod.OfYear(1995)));
        Assert.Contains("SIM-DOMAT has no files before 1996 in the folder layouts the catalog knows.", beforeMaternal.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.Sim.DOFET.GetNationalPath(DatasusPeriod.OfYear(1978)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.SihSus.MT.GetNationalPath(DatasusPeriod.OfMonth(1994, 12)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DatasusCatalog.EsusNotifica.DCCR.GetNationalPath(DatasusPeriod.OfYear(2022)));
    }

    // Two digits of the year name a year of the hundred that start with the folder, not with the
    // first file: the folder of DOREXT starts in 1996, though its first file is of 2013.
    [Fact]
    public void TwoDigitYear_IsReadInTheHundredYearsOfTheFolder()
    {
        var abroad = DatasusCatalog.Sim.DOREXT.CurrentEra;
        var external = DatasusCatalog.Sim.DOEXT;

        Assert.True(abroad.TryParseNationalFileName("DOREXT12.dbc", out var period));
        Assert.Equal(DatasusPeriod.OfYear(2012), period);
        Assert.True(abroad.TryParseNationalFileName("DOREXT96.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(1996), period);
        Assert.True(abroad.TryParseNationalFileName("DOREXT95.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(2095), period);
        Assert.Equal(2095, abroad.LastNameableYear);

        // DOEXT96.dbc is 1996 in the folder in use now; the older folder ends in 1995 and has no such file.
        Assert.True(external.CurrentEra.TryParseNationalFileName("DOEXT96.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(1996), period);
        Assert.False(external.Eras[0].TryParseNationalFileName("DOEXT96.dbc", out _));
        Assert.True(external.Eras[0].TryParseNationalFileName("DOEXT95.DBC", out period));
        Assert.Equal(DatasusPeriod.OfYear(1995), period);
        Assert.True(external.Eras[0].TryParseNationalFileName("DOEXT79.DBC", out period));
        Assert.Equal(DatasusPeriod.OfYear(1979), period);
        Assert.False(external.Eras[0].TryParseNationalFileName("DOEXT78.dbc", out _));

        var beyond = Assert.Throws<ArgumentOutOfRangeException>(() => external.GetNationalPath(DatasusPeriod.OfYear(2096)));
        Assert.Contains("SIM-DOEXT file names carry two digits of the year, so the catalog builds them only up to 2095: 2096 would get the name of 1996.", beyond.Message, StringComparison.Ordinal);
        Assert.Equal("/painel_oncologia/Dados/POBR2096.dbc", DatasusCatalog.Po.PO.GetNationalPath(DatasusPeriod.OfYear(2096)));
    }

    // The name of one file type is never read as the name of another of the same folder.
    [Theory]
    [InlineData("SIM", "DOEXT", "DOREXT13.dbc")]
    [InlineData("SIM", "DOEXT", "DOFET13.dbc")]
    [InlineData("SIM", "DOEXT", "DOINF13.dbc")]
    [InlineData("SIM", "DOEXT", "DOMAT13.dbc")]
    [InlineData("SIM", "DOEXT", "DOEXT2013.dbc")]
    [InlineData("SIM", "DOEXT", "DOEXT13a.dbc")]
    [InlineData("SIM", "DOREXT", "DOEXT13.dbc")]
    [InlineData("SIM", "DOREXT", "DOREXT2013.dbc")]
    [InlineData("SIM", "DOFET", "DOEXT13.dbc")]
    [InlineData("SIM", "DOFET", "DOFETO13.dbc")]
    [InlineData("SIM", "DOINF", "DOMAT13.dbc")]
    [InlineData("SIM", "DOMAT", "DOINF13.dbc")]
    [InlineData("SIM", "DOMAT", "DOBR2013.dbc")]
    [InlineData("SIM", "DO", "DOEXT13.dbc")]
    [InlineData("SIM", "DO", "DOREXT13.dbc")]
    [InlineData("SINASC", "DNEX", "DNEX25.dbc")]       // the second name of 2025 in the preliminary folder, with two digits
    [InlineData("SINASC", "DNEX", "DNEX26.dbc")]
    [InlineData("SINASC", "DNEX", "DNBR2020.dbc")]
    [InlineData("SINASC", "DNEX", "DNRR2020.dbc")]
    [InlineData("SINASC", "DN", "DNEX2020.dbc")]
    [InlineData("SIHSUS", "CH", "CMBR2001.dbc")]
    [InlineData("SIHSUS", "CH", "CHSP2001.dbc")]
    [InlineData("SIHSUS", "CH", "CHBR200.dbc")]
    [InlineData("SIHSUS", "CM", "CHBR2001.dbc")]
    [InlineData("SIHSUS", "CM", "CMPI1201.dbc")]       // the SISMAMA file with the same code, of a state
    [InlineData("SIHSUS", "MT", "MTBR96.dbc")]
    [InlineData("SIHSUS", "MT", "MTBR9613.dbc")]
    [InlineData("SIHSUS", "RD", "CHBR2001.dbc")]
    [InlineData("PO", "PO", "POBR13.dbc")]
    [InlineData("PO", "PO", "POSP2013.dbc")]
    [InlineData("ESUSNOTIFICA", "DCCR", "DCCRBR2023.dbc")]
    [InlineData("ESUSNOTIFICA", "DCCR", "DCCBR23.dbc")]
    public void FileNameOfAnotherFileType_IsReadByNoLayoutOfTheDataset(string source, string code, string fileName)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.All(dataset.Eras, era =>
        {
            Assert.False(era.TryParseNationalFileName(fileName, out _), $"{dataset} read {fileName} as its file for the whole country");
            Assert.False(era.TryParseFileName(fileName, out _, out _, out _), $"{dataset} read {fileName} as the file of a state");
        });
    }

    // SIHSUS and SISMAMA each have a file type with the code CM. They are two datasets, and a
    // dataset is looked up by its source and its code together.
    [Fact]
    public void SameCodeInTwoSources_AreTwoDatasets()
    {
        var ofSih = DatasusCatalog.SihSus.CM;
        var ofSisMama = DatasusCatalog.SisMama.CM;

        Assert.NotSame(ofSih, ofSisMama);
        Assert.Equal(("SIHSUS", "CM", "SIHSUS-CM"), (ofSih.Source, ofSih.Code, ofSih.ToString()));
        Assert.Equal(("SISMAMA", "CM", "SISMAMA-CM"), (ofSisMama.Source, ofSisMama.Code, ofSisMama.ToString()));
        Assert.Same(ofSih, DatasusCatalog.Get("SIHSUS", "CM"));
        Assert.Same(ofSisMama, DatasusCatalog.Get("SISMAMA", "CM"));
        Assert.Same(ofSih, DatasusCatalog.Get("sihsus", "cm"));
        Assert.Equal(DatasusCoverage.National, ofSih.Coverage);
        Assert.Equal(DatasusCoverage.Uf, ofSisMama.Coverage);
        Assert.Null(ofSih.OfficialName);
        Assert.Equal("Citopatológico de Mama", ofSisMama.OfficialName);
        Assert.Equal("/SIHSUS/200801_/Dados/CMBR2001.dbc", ofSih.GetNationalPath(DatasusPeriod.OfMonth(2020, 1)));
        Assert.Equal("/SISCAN/SISMAMA/Dados/CMPI1201.dbc", ofSisMama.GetPath(DatasusUf.PI, DatasusPeriod.OfMonth(2012, 1)));

        // Neither reads the file of the other, and each refuses the methods of the other kind.
        Assert.False(ofSih.CurrentEra.TryParseNationalFileName("CMPI1201.dbc", out _));
        Assert.False(ofSisMama.CurrentEra.TryParseFileName("CMBR2001.dbc", out _, out _, out _));
        Assert.Throws<NotSupportedException>(() => ofSih.GetPath(DatasusUf.PI, DatasusPeriod.OfMonth(2020, 1)));
        Assert.Throws<NotSupportedException>(() => ofSisMama.GetNationalPath(DatasusPeriod.OfMonth(2012, 1)));

        // The code alone names nothing.
        Assert.False(DatasusCatalog.TryGet(string.Empty, "CM", out _));
        Assert.False(DatasusCatalog.TryGet("CM", "CM", out _));
        Assert.False(DatasusCatalog.TryGet("SISCOLO", "CM", out _));
        Assert.Equal(2, DatasusCatalog.Datasets.Count(dataset => dataset.Code == "CM"));
    }

    // DATASUS publishes no name for three file types: the name is null, never one made up here.
    [Theory]
    [InlineData("SIHSUS", "CH")]
    [InlineData("SIHSUS", "CM")]
    [InlineData("SIHSUS", "MT")]
    public void OfficialName_IsNull_ForAFileTypeThatDatasusDoesNotName(string source, string code)
    {
        var dataset = DatasusCatalog.Get(source, code);

        Assert.Null(dataset.OfficialName);
        Assert.Equal(source + "-" + code, dataset.ToString());
    }

    [Fact]
    public async Task ListNationalTablesAsync_FolderSharedByFileTypes_GivesEachDatasetItsOwnTables()
    {
        Add(SimNow, ("DOEXT20.dbc", 1), ("DOFET20.dbc", 2), ("DOINF20.dbc", 3), ("DOMAT20.dbc", 4), ("DOREXT20.dbc", 5), ("DOREXT13.dbc", 6), ("DOEXT13.DBC", 7), ("DOBR2020.dbc", 9));
        Add(SimPreliminary, ("DOEXT26.dbc", 8), ("DOREXT26.dbc", 9), ("DOEXT20.dbc", 99));
        Add(SimOlder, ("DOEXT95.DBC", 10), ("DOFET95.DBC", 11), ("DOINF95.DBC", 12), ("DOEXT96.dbc", 99));
        var client = _fixture.Client();
        var from = DatasusPeriod.OfYear(1995);
        var to = DatasusPeriod.OfYear(2026);

        var external = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DOEXT, from, to, Ct);
        var fetal = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DOFET, from, to, Ct);
        var infant = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DOINF, from, to, Ct);
        var maternal = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DOMAT, DatasusPeriod.OfYear(1996), to, Ct);
        var abroad = await client.ListNationalTablesAsync(DatasusCatalog.Sim.DOREXT, DatasusPeriod.OfYear(1996), to, Ct);

        Assert.Equal(
            [(SimOlder + "/DOEXT95.DBC", "1995", false), (SimNow + "/DOEXT13.DBC", "2013", false), (SimNow + "/DOEXT20.dbc", "2020", false), (SimPreliminary + "/DOEXT26.dbc", "2026", true)],
            external.Select(table => (table.Path, table.Period.ToString(), table.IsPreliminary)));
        Assert.Equal([SimOlder + "/DOFET95.DBC", SimNow + "/DOFET20.dbc"], fetal.Select(table => table.Path));
        Assert.Equal([SimOlder + "/DOINF95.DBC", SimNow + "/DOINF20.dbc"], infant.Select(table => table.Path));
        Assert.Equal([SimNow + "/DOMAT20.dbc"], maternal.Select(table => table.Path));
        Assert.Equal([SimNow + "/DOREXT13.dbc", SimNow + "/DOREXT20.dbc", SimPreliminary + "/DOREXT26.dbc"], abroad.Select(table => table.Path));
        Assert.All(external.Concat(fetal).Concat(infant).Concat(maternal).Concat(abroad), table => Assert.Null(table.Uf));
        Assert.Equal(0, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FolderOfSihsus_GivesTheFilesOfTheCountryAndThoseOfTheStates_EachToItsDataset()
    {
        Add(Sih, ("CHBR2001.dbc", 1), ("CMBR2001.dbc", 2), ("RDSP2001.dbc", 3), ("ERSP2001.dbc", 4), ("CHBR2002.dbc", 9));
        var client = _fixture.Client();
        var month = DatasusPeriod.OfMonth(2020, 1);

        var hospitals = await client.ListNationalTablesAsync(DatasusCatalog.SihSus.CH, month, month, Ct);
        var maintainers = await client.ListNationalTablesAsync(DatasusCatalog.SihSus.CM, month, month, Ct);
        var reduced = await client.ListTablesAsync(DatasusCatalog.SihSus.RD, month, month, cancellationToken: Ct);

        Assert.Equal([Sih + "/CHBR2001.dbc"], hospitals.Select(table => table.Path));
        Assert.Equal([Sih + "/CMBR2001.dbc"], maintainers.Select(table => table.Path));
        Assert.Equal([Sih + "/RDSP2001.dbc"], reduced.Select(table => table.Path));
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task FindNationalTableAsync_AndOpenNationalReaderAsync_WorkForCountryOnlyFileTypes()
    {
        _fixture.Server.Add("/SINASC/PRELIM/DNRES/DNEX2025.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add("/SINASC/PRELIM/DNRES/DNEX25.dbc", ClientFixture.Dbc("X1"));
        _fixture.Server.Add("/painel_oncologia/Dados/POBR2013.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add("/SIHSUS/Arquivos_MTBR/MTBR9612.dbc", ClientFixture.Dbc("M1"));
        _fixture.Server.Add(SimOlder + "/DOFET90.DBC", ClientFixture.Dbc("F1"));
        var client = _fixture.Client();

        var births = await client.FindNationalTableAsync(DatasusCatalog.Sinasc.DNEX, DatasusPeriod.OfYear(2025), Ct);
        var older = await client.FindNationalTableAsync(DatasusCatalog.Sim.DOFET, DatasusPeriod.OfYear(1990), Ct);

        Assert.Equal(("/SINASC/PRELIM/DNRES/DNEX2025.dbc", true), (births.Path, births.IsPreliminary));
        Assert.Null(births.Uf);
        Assert.Equal((SimOlder + "/DOFET90.DBC", false), (older.Path, older.IsPreliminary));
        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Po.PO, DatasusPeriod.OfYear(2013), cancellationToken: Ct))
        {
            Assert.Equal(["A1", "A2"], Codes(reader));
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.SihSus.MT, DatasusPeriod.OfMonth(1996, 12), cancellationToken: Ct))
        {
            Assert.Equal(["M1"], Codes(reader));
        }
    }

    // The message of a table that is not there says that it is of the whole country.
    [Fact]
    public async Task FindNationalTableAsync_NoTable_ThrowsSayingItIsOfTheWholeCountry()
    {
        _fixture.Server.Add(SimNow + "/DOEXT31.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add("/SINASC/PRELIM/DNRES/DNEX2025.dbc", ClientFixture.Dbc("P1"));
        var client = _fixture.Client();

        var fetal = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.Sim.DOFET, DatasusPeriod.OfYear(2031), Ct));
        var oncology = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.Po.PO, DatasusPeriod.OfYear(2030), Ct));
        var hospitals = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.SihSus.CH, DatasusPeriod.OfMonth(2018, 12), Ct));
        var onlyPreliminary = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sinasc.DNEX, DatasusPeriod.OfYear(2025), cancellationToken: Ct));

        Assert.Equal("The server has no SIM-DOFET table of the whole country for 2031, final or preliminary: neither /SIM/CID10/DOFET/DOFET31.dbc nor /SIM/PRELIM/DOFET/DOFET31.dbc exists.", fetal.Message);
        Assert.Equal("The server has no PO-PO table of the whole country for 2030: /painel_oncologia/Dados/POBR2030.dbc does not exist, whole or in parts.", oncology.Message);
        Assert.Equal("The server has no SIHSUS-CH table of the whole country for 2018-12: /SIHSUS/200801_/Dados/CHBR1812.dbc does not exist, whole or in parts.", hospitals.Message);
        Assert.StartsWith("SINASC-DNEX of the whole country for 2025 is not published as final data: only the preliminary table /SINASC/PRELIM/DNRES/DNEX2025.dbc exists,", onlyPreliminary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MethodsByState_AreRefusedForEveryCountryOnlyFileType_AndTheServerIsNotAsked()
    {
        var client = _fixture.Client();

        foreach (var dataset in DatasusCatalog.Datasets.Where(dataset => dataset.Coverage == DatasusCoverage.National))
        {
            var period = dataset.Periodicity == DatasusPeriodicity.Monthly ? DatasusPeriod.OfMonth(2020, 1) : DatasusPeriod.OfYear(2024);
            var find = await Assert.ThrowsAsync<ArgumentException>(() => client.FindTableAsync(dataset, DatasusUf.SP, period, Ct));
            var open = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(dataset, DatasusUf.SP, period, cancellationToken: Ct));
            var list = await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(dataset, period, period, cancellationToken: Ct));

            Assert.All(new[] { find, open, list }, exception =>
            {
                Assert.Equal("dataset", exception.ParamName);
                Assert.StartsWith($"{dataset} has one file for the whole country and none by state: use FindNationalTableAsync, OpenNationalReaderAsync or ListNationalTablesAsync.", exception.Message, StringComparison.Ordinal);
            });
        }

        Assert.Equal(11, DatasusCatalog.Datasets.Count(dataset => dataset.Coverage == DatasusCoverage.National));
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
