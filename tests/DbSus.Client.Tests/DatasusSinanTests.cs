using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// SINAN: 57 file types, one per notifiable condition, each with one file per year for the whole
// country and none by state. The file names are real ones, as the DATASUS server listed them in
// October 2026.
public sealed class DatasusSinanTests : IDisposable
{
    private const string Final = "/SINAN/DADOS/FINAIS";
    private const string Preliminary = "/SINAN/DADOS/PRELIM";

    // The codes, in the order of the catalog.
    private static readonly string[] Codes =
    [
        "ANIM", "ANTR", "AIDA", "AIDC", "BOTU", "COLE", "COQU", "DENG", "DIFT", "DCRJ", "CHAG", "EXAN", "ESQU", "ESPO", "CHIK", "FMAC", "FTIF", "HANS", "HANT",
        "HEPA", "HIVA", "HIVC", "HIVE", "HIVG", "IEXO", "LEIV", "LTAN", "LEPT", "MALA", "MENI", "PFAN", "PEST", "RAIV", "ROTA", "SIFA", "SIFC", "SIFG", "SRC",
        "SDTA", "TETA", "TETN", "TOXC", "TOXG", "NTRA", "TRAC", "TUBE", "VARC", "VIOL", "ZIKA", "ACBI", "ACGR", "CANC", "DERM", "LERD", "PAIR", "PNEU", "MENT",
    ];

    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static List<DatasusDataset> Sinan => DatasusCatalog.Datasets.Where(dataset => dataset.Source == "SINAN").ToList();

    public void Dispose() => _fixture.Dispose();

    // The count of the catalog, by what each file type covers, computed from the catalog itself.
    [Fact]
    public void Catalog_Has111FileTypesOf15Sources_And68OfThemAreForTheWholeCountryOnly()
    {
        var datasets = DatasusCatalog.Datasets;

        Assert.Equal(111, datasets.Count);
        Assert.Equal(15, datasets.Select(dataset => dataset.Source).Distinct().Count());
        Assert.Equal("SINAN", datasets.Select(dataset => dataset.Source).Distinct().Last());
        Assert.Equal(39, datasets.Count(dataset => dataset.Coverage == DatasusCoverage.Uf));
        Assert.Equal(4, datasets.Count(dataset => dataset.Coverage == (DatasusCoverage.Uf | DatasusCoverage.National)));
        Assert.Equal(68, datasets.Count(dataset => dataset.Coverage == DatasusCoverage.National));
        Assert.Equal(57, Sinan.Count);
        Assert.Equal(Codes, Sinan.Select(dataset => dataset.Code));
        Assert.Equal(54, datasets.TakeWhile(dataset => dataset.Source != "SINAN").Count());
    }

    [Fact]
    public void EverySinanFileType_IsYearly_ForTheWholeCountryOnly_InTheTwoFolders_WithTwoDigitsOfTheYear()
    {
        Assert.All(Sinan, dataset =>
        {
            var era = Assert.Single(dataset.Eras);

            Assert.Equal(DatasusCoverage.National, dataset.Coverage);
            Assert.Equal(DatasusPeriodicity.Yearly, dataset.Periodicity);
            Assert.False(string.IsNullOrWhiteSpace(dataset.OfficialName), dataset.ToString());
            Assert.Equal((Final, Preliminary), (era.Directory, era.PreliminaryDirectory));
            Assert.Null(era.LastPeriod);
            Assert.Null(era.StatePrefix);
            Assert.Equal(dataset.Code + "BR{YY}.dbc", era.NationalFileNamePattern);
            Assert.Same(dataset, DatasusCatalog.Get("SINAN", dataset.Code));
            Assert.Same(dataset, typeof(DatasusCatalog.Sinan).GetProperty(dataset.Code)!.GetValue(null));
        });
    }

    // One real file of each folder, of file types that are in one folder, in the other, or in both.
    [Theory]
    [InlineData("DENG", 2000, "/SINAN/DADOS/FINAIS/DENGBR00.dbc", "/SINAN/DADOS/PRELIM/DENGBR00.dbc")]
    [InlineData("DENG", 2026, "/SINAN/DADOS/FINAIS/DENGBR26.dbc", "/SINAN/DADOS/PRELIM/DENGBR26.dbc")]   // listed in the preliminary folder
    [InlineData("HANT", 1999, "/SINAN/DADOS/FINAIS/HANTBR99.dbc", "/SINAN/DADOS/PRELIM/HANTBR99.dbc")]
    [InlineData("AIDA", 2024, "/SINAN/DADOS/FINAIS/AIDABR24.dbc", "/SINAN/DADOS/PRELIM/AIDABR24.dbc")]   // only in the preliminary folder
    [InlineData("SRC", 2026, "/SINAN/DADOS/FINAIS/SRCBR26.dbc", "/SINAN/DADOS/PRELIM/SRCBR26.dbc")]      // the code with three letters
    [InlineData("LTAN", 2025, "/SINAN/DADOS/FINAIS/LTANBR25.dbc", "/SINAN/DADOS/PRELIM/LTANBR25.dbc")]   // only in the final folder
    [InlineData("LERD", 2019, "/SINAN/DADOS/FINAIS/LERDBR19.dbc", "/SINAN/DADOS/PRELIM/LERDBR19.dbc")]
    public void GetNationalPath_AndCandidates_AreTheFinalPathAndThenThePreliminaryOne(string code, int year, string finalPath, string preliminaryPath)
    {
        var dataset = DatasusCatalog.Get("SINAN", code);
        var period = DatasusPeriod.OfYear(year);

        Assert.Equal(finalPath, dataset.GetNationalPath(period));
        Assert.Equal([finalPath, preliminaryPath], dataset.GetNationalCandidatePaths(period));
        Assert.True(dataset.CurrentEra.TryParseNationalFileName(finalPath.Substring(finalPath.LastIndexOf('/') + 1), out var readBack));
        Assert.Equal(period, readBack);
    }

    // The first period of each file type is the year of its first file in the listings of
    // October 2026, in either folder; the two digits of the year name the hundred years from it on.
    [Theory]
    [InlineData("HANT", 1999)]
    [InlineData("DENG", 2000)]
    [InlineData("TUBE", 2001)]
    [InlineData("MALA", 2004)]
    [InlineData("ACBI", 2006)]
    [InlineData("AIDA", 2007)]   // only in the preliminary folder
    [InlineData("TRAC", 2009)]
    [InlineData("NTRA", 2010)]
    [InlineData("ESPO", 2013)]
    [InlineData("CHIK", 2014)]
    [InlineData("HIVE", 2015)]
    [InlineData("TOXC", 2019)]
    public void FirstPeriod_IsTheFirstFileListed_AndTheTwoDigitsNameAHundredYearsFromIt(string code, int firstYear)
    {
        var dataset = DatasusCatalog.Get("SINAN", code);
        var era = dataset.CurrentEra;
        var first = (firstYear % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        var before = ((firstYear + 99) % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(DatasusPeriod.OfYear(firstYear), dataset.FirstPeriod);
        Assert.Equal(firstYear + 99, era.LastNameableYear);
        Assert.Equal($"{Final}/{code}BR{first}.dbc", dataset.GetNationalPath(DatasusPeriod.OfYear(firstYear)));
        Assert.Equal($"{Final}/{code}BR{before}.dbc", dataset.GetNationalPath(DatasusPeriod.OfYear(firstYear + 99)));
        Assert.True(era.TryParseNationalFileName($"{code}BR{first}.dbc", out var period));
        Assert.Equal(DatasusPeriod.OfYear(firstYear), period);
        Assert.True(era.TryParseNationalFileName($"{code}BR{before}.dbc", out period));
        Assert.Equal(DatasusPeriod.OfYear(firstYear + 99), period);

        Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetNationalPath(DatasusPeriod.OfYear(firstYear - 1)));
        var beyond = Assert.Throws<ArgumentOutOfRangeException>(() => dataset.GetNationalPath(DatasusPeriod.OfYear(firstYear + 100)));
        Assert.Contains($"SINAN-{code} file names carry two digits of the year, so the catalog builds them only up to {firstYear + 99}", beyond.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => dataset.GetNationalPath(DatasusPeriod.OfMonth(firstYear, 1)));
    }

    // Every file type: the window of a hundred years starts at its own first year, whatever it is.
    [Fact]
    public void EverySinanFileType_ReadsItsOwnNamesInItsOwnHundredYears()
    {
        Assert.All(Sinan, dataset =>
        {
            var era = dataset.CurrentEra;
            var firstYear = dataset.FirstPeriod.Year;

            Assert.InRange(firstYear, 1999, 2019);
            Assert.Equal(firstYear + 99, era.LastNameableYear);
            for (var year = firstYear; year < firstYear + 100; year++)
            {
                var name = dataset.GetNationalPath(DatasusPeriod.OfYear(year)).Substring(Final.Length + 1);

                Assert.Equal($"{dataset.Code}BR{year % 100:00}.dbc", name);
                Assert.True(era.TryParseNationalFileName(name, out var period), name);
                Assert.Equal(DatasusPeriod.OfYear(year), period);
            }
        });
    }

    // A name is the stem, two digits and the extension, and nothing else; the letter case does not matter.
    [Fact]
    public void EverySinanFileType_ReadsOnlyNamesOfItsExactLength()
    {
        Assert.All(Sinan, dataset =>
        {
            var era = dataset.CurrentEra;
            var code = dataset.Code;

            Assert.True(era.TryParseNationalFileName($"{code}BR24.dbc", out _));
            Assert.True(era.TryParseNationalFileName($"{code.ToLowerInvariant()}br24.DBC", out _));
            Assert.All(
                new[] { $"{code}BR2024.dbc", $"{code}BR4.dbc", $"{code}BR24a.dbc", $"{code}BR24_1.dbc", $"{code}BR.dbc", $"{code}SP24.dbc", $"{code}24.dbc", $"{code}BR24.dbf", $"{code}BR2401.dbc", $"X{code}BR24.dbc", $"{code}XBR24.dbc" },
                name => Assert.False(era.TryParseNationalFileName(name, out _), $"{dataset} read {name}"));
            Assert.False(era.TryParseFileName($"{code}BR24.dbc", out _, out _, out _), $"{dataset} read its file as the file of a state");
            Assert.False(era.TryParseFileName($"{code}SP24.dbc", out _, out _, out _));
        });
    }

    // Each name of a SINAN file is read by its own file type and by no other of the whole catalog,
    // in any folder layout, as the file of a state or as the file for the whole country.
    [Fact]
    public void NameOfASinanFile_IsReadByExactlyOneDatasetOfTheCatalog()
    {
        foreach (var code in Codes)
        {
            var name = $"{code}BR24.dbc";
            var readers = DatasusCatalog.Datasets
                .Where(dataset => dataset.Eras.Any(era => era.TryParseNationalFileName(name, out _) || era.TryParseFileName(name, out _, out _, out _)))
                .Select(dataset => dataset.ToString())
                .ToList();

            Assert.Equal(["SINAN-" + code], readers);
        }
    }

    // Names of the two folders, or like them, that are of no file type of the catalog. LERBR19.dbc
    // was listed in October 2026 beside LERDBR19.dbc; the website lists INFL, and the server had no
    // file of it.
    [Theory]
    [InlineData("LERBR19.dbc")]
    [InlineData("INFLBR19.dbc")]
    [InlineData("SRCSBR24.dbc")]
    [InlineData("SRBR24.dbc")]
    [InlineData("DENGONBR24.dbc")]
    [InlineData("DENGBR.dbc")]
    [InlineData("HIVBR24.dbc")]
    [InlineData("SIFBR24.dbc")]
    [InlineData("TOXBR24.dbc")]
    [InlineData("DCCRBR2023.dbc")]
    public void NameOutsideTheCatalog_IsReadByNoDataset(string fileName)
    {
        Assert.All(DatasusCatalog.Datasets, dataset => Assert.All(dataset.Eras, era =>
        {
            Assert.False(era.TryParseNationalFileName(fileName, out _), $"{dataset} read {fileName} as its file for the whole country");
            Assert.False(era.TryParseFileName(fileName, out _, out _, out _), $"{dataset} read {fileName} as the file of a state");
        }));
    }

    // Names of other sources are not read as SINAN, and names of SINAN are not read by other sources.
    [Theory]
    [InlineData("DCCRBR23.dbc", "ESUSNOTIFICA-DCCR")]
    [InlineData("PCEBR95.dbc", "PCE-PCE")]
    [InlineData("RESPBR24.dbc", "RESP-RESP")]
    [InlineData("POBR2013.dbc", "PO-PO")]
    [InlineData("DOBR2011.dbc", "SIM-DO")]
    [InlineData("DORBR95.dbc", "SIM-DO")]
    [InlineData("DNBR2020.dbc", "SINASC-DN")]
    [InlineData("CHBR2001.dbc", "SIHSUS-CH")]
    [InlineData("MTBR9612.dbc", "SIHSUS-MT")]
    public void NameOfAnotherSource_IsNotReadAsSinan(string fileName, string owner)
    {
        var readers = DatasusCatalog.Datasets
            .Where(dataset => dataset.Eras.Any(era => era.TryParseNationalFileName(fileName, out _)))
            .Select(dataset => dataset.ToString());

        Assert.Equal([owner], readers);
    }

    // The codes that share letters: each reads its own name only.
    [Theory]
    [InlineData("LERD", "LERDBR19.dbc")]
    [InlineData("SRC", "SRCBR26.dbc")]
    [InlineData("HIVA", "HIVABR24.dbc")]
    [InlineData("HIVC", "HIVCBR24.dbc")]
    [InlineData("HIVE", "HIVEBR24.dbc")]
    [InlineData("HIVG", "HIVGBR24.dbc")]
    [InlineData("AIDA", "AIDABR24.dbc")]
    [InlineData("AIDC", "AIDCBR24.dbc")]
    [InlineData("SIFA", "SIFABR25.dbc")]
    [InlineData("SIFC", "SIFCBR25.dbc")]
    [InlineData("SIFG", "SIFGBR25.dbc")]
    [InlineData("TETA", "TETABR23.dbc")]
    [InlineData("TETN", "TETNBR21.dbc")]
    [InlineData("TOXC", "TOXCBR23.dbc")]
    [InlineData("TOXG", "TOXGBR23.dbc")]
    [InlineData("NTRA", "NTRABR24.dbc")]
    [InlineData("TRAC", "TRACBR24.dbc")]
    [InlineData("ACBI", "ACBIBR22.dbc")]
    [InlineData("ACGR", "ACGRBR22.dbc")]
    [InlineData("LEIV", "LEIVBR25.dbc")]
    [InlineData("LEPT", "LEPTBR24.dbc")]
    [InlineData("HANS", "HANSBR23.dbc")]
    [InlineData("HANT", "HANTBR24.dbc")]
    public void CodesThatShareLetters_EachReadsItsOwnNameOnly(string code, string fileName)
    {
        Assert.All(Sinan, dataset => Assert.Equal(dataset.Code == code, dataset.CurrentEra.TryParseNationalFileName(fileName, out _)));
    }

    [Fact]
    public void MethodsByState_AreRefusedForSinan()
    {
        Assert.All(Sinan, dataset =>
        {
            var path = Assert.Throws<NotSupportedException>(() => dataset.GetPath(DatasusUf.SP, DatasusPeriod.OfYear(2024)));
            var candidates = Assert.Throws<NotSupportedException>(() => dataset.GetCandidatePaths(DatasusUf.SP, DatasusPeriod.OfYear(2024)));

            Assert.Equal($"{dataset} has one file for the whole country and none by state: use GetNationalPath or GetNationalCandidatePaths.", path.Message);
            Assert.Equal(path.Message, candidates.Message);
        });
    }

    [Fact]
    public async Task ClientMethodsByState_AreRefusedForSinan_AndTheServerIsNotAsked()
    {
        var client = _fixture.Client();
        var year = DatasusPeriod.OfYear(2024);

        foreach (var dataset in new[] { DatasusCatalog.Sinan.DENG, DatasusCatalog.Sinan.AIDA, DatasusCatalog.Sinan.SRC, DatasusCatalog.Sinan.MENT })
        {
            var find = await Assert.ThrowsAsync<ArgumentException>(() => client.FindTableAsync(dataset, DatasusUf.SP, year, Ct));
            var open = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(dataset, DatasusUf.SP, year, cancellationToken: Ct));
            var list = await Assert.ThrowsAsync<ArgumentException>(() => client.ListTablesAsync(dataset, year, year, cancellationToken: Ct));

            Assert.All(new[] { find, open, list }, exception =>
            {
                Assert.Equal("dataset", exception.ParamName);
                Assert.StartsWith($"{dataset} has one file for the whole country and none by state: use FindNationalTableAsync, OpenNationalReaderAsync or ListNationalTablesAsync.", exception.Message, StringComparison.Ordinal);
            });
        }

        Assert.Equal(0, _fixture.Server.ListCalls + _fixture.Server.InfoCalls);
    }

    // The two folders hold the files of every notifiable condition: each dataset gets its own.
    [Fact]
    public async Task ListNationalTablesAsync_FoldersSharedByEveryFileType_GiveEachDatasetItsOwnTables()
    {
        Add(Final, ("DENGBR00.dbc", 1), ("DENGBR24.dbc", 2), ("DENGBR25.dbc", 3), ("CHIKBR24.dbc", 4), ("LERDBR19.dbc", 5), ("LERBR19.dbc", 99), ("HANTBR99.dbc", 6), ("HANTBR24.dbc", 7), ("LTANBR25.dbc", 8), ("ACGRBR22.dbc", 9), ("ACBIBR22.dbc", 10));
        Add(Preliminary, ("DENGBR26.dbc", 11), ("AIDABR07.dbc", 12), ("AIDABR24.dbc", 13), ("AIDCBR24.dbc", 14), ("SRCBR26.dbc", 15), ("HANTBR25.dbc", 16), ("ACGRBR23.dbc", 17));
        var client = _fixture.Client();
        var from = DatasusPeriod.OfYear(2007);
        var to = DatasusPeriod.OfYear(2026);

        var dengue = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.DENG, DatasusPeriod.OfYear(2000), to, Ct);
        var hantavirus = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.HANT, DatasusPeriod.OfYear(1999), to, Ct);
        var strain = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.LERD, from, to, Ct);
        var aids = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.AIDA, from, to, Ct);
        var rubella = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.SRC, from, to, Ct);
        var leishmaniasis = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.LTAN, from, to, Ct);
        var accidents = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.ACGR, from, to, Ct);
        var cholera = await client.ListNationalTablesAsync(DatasusCatalog.Sinan.COLE, from, to, Ct);

        Assert.Equal(
            [(Final + "/DENGBR00.dbc", "2000", false), (Final + "/DENGBR24.dbc", "2024", false), (Final + "/DENGBR25.dbc", "2025", false), (Preliminary + "/DENGBR26.dbc", "2026", true)],
            dengue.Select(table => (table.Path, table.Period.ToString(), table.IsPreliminary)));
        Assert.Equal([Final + "/HANTBR99.dbc", Final + "/HANTBR24.dbc", Preliminary + "/HANTBR25.dbc"], hantavirus.Select(table => table.Path));
        Assert.Equal(["1999", "2024", "2025"], hantavirus.Select(table => table.Period.ToString()));
        Assert.Equal([Final + "/LERDBR19.dbc"], strain.Select(table => table.Path));
        Assert.Equal([(Preliminary + "/AIDABR07.dbc", true), (Preliminary + "/AIDABR24.dbc", true)], aids.Select(table => (table.Path, table.IsPreliminary)));
        Assert.Equal([Preliminary + "/SRCBR26.dbc"], rubella.Select(table => table.Path));
        Assert.Equal([(Final + "/LTANBR25.dbc", false)], leishmaniasis.Select(table => (table.Path, table.IsPreliminary)));
        Assert.Equal([Final + "/ACGRBR22.dbc", Preliminary + "/ACGRBR23.dbc"], accidents.Select(table => table.Path));
        Assert.Empty(cholera);
        Assert.All(dengue.Concat(hantavirus).Concat(strain).Concat(aids).Concat(rubella).Concat(leishmaniasis).Concat(accidents), table => Assert.Null(table.Uf));

        // One listing of each folder served the eight datasets, and no file was asked for.
        Assert.Equal(2, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);
    }

    // A notifiable condition whose files are all in the preliminary folder: the final folder is
    // asked first, the table is found in the preliminary one, and it is opened only by choice.
    [Fact]
    public async Task FileTypeOnlyInThePreliminaryFolder_IsFoundThere_AndNotOpenedAsFinalData()
    {
        _fixture.Server.Add(Preliminary + "/AIDABR24.dbc", ClientFixture.Dbc("P1", "P2"));
        _fixture.Server.Add(Final + "/DENGBR24.dbc", ClientFixture.Dbc("F1"));
        var client = _fixture.Client();
        var year = DatasusPeriod.OfYear(2024);

        var preliminary = await client.FindNationalTableAsync(DatasusCatalog.Sinan.AIDA, year, Ct);
        var final = await client.FindNationalTableAsync(DatasusCatalog.Sinan.DENG, year, Ct);
        var notFinal = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sinan.AIDA, year, cancellationToken: Ct));
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindNationalTableAsync(DatasusCatalog.Sinan.CHIK, year, Ct));

        Assert.Equal((Preliminary + "/AIDABR24.dbc", true), (preliminary.Path, preliminary.IsPreliminary));
        Assert.Null(preliminary.Uf);
        Assert.Equal((Final + "/DENGBR24.dbc", false), (final.Path, final.IsPreliminary));
        Assert.StartsWith("SINAN-AIDA of the whole country for 2024 is not published as final data: only the preliminary table /SINAN/DADOS/PRELIM/AIDABR24.dbc exists,", notFinal.Message, StringComparison.Ordinal);
        Assert.Equal("The server has no SINAN-CHIK table of the whole country for 2024, final or preliminary: neither /SINAN/DADOS/FINAIS/CHIKBR24.dbc nor /SINAN/DADOS/PRELIM/CHIKBR24.dbc exists.", missing.Message);

        using var reader = await client.OpenReaderAsync(preliminary, cancellationToken: Ct);
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        Assert.Equal(["P1", "P2"], codes);
    }

    [Fact]
    public void Get_FindsSinanByItsSourceAndCode_AndRefusesWhatIsNotAFileTypeOfTheCatalog()
    {
        Assert.Same(DatasusCatalog.Sinan.DENG, DatasusCatalog.Get("SINAN", "DENG"));
        Assert.Same(DatasusCatalog.Sinan.DENG, DatasusCatalog.Get("sinan", "deng"));
        Assert.Same(DatasusCatalog.Sinan.SRC, DatasusCatalog.Get("Sinan", "Src"));
        Assert.Equal("SINAN-LERD", DatasusCatalog.Sinan.LERD.ToString());

        // INFL is listed by the DATASUS website and had no file on the server in October 2026; LER
        // is the stem of one stray file. Neither is a file type of the catalog.
        Assert.False(DatasusCatalog.TryGet("SINAN", "INFL", out _));
        Assert.False(DatasusCatalog.TryGet("SINAN", "LER", out _));
        Assert.False(DatasusCatalog.TryGet("SINAN", "DENGBR", out _));
        Assert.False(DatasusCatalog.TryGet("SINAN_P", "DENG", out _));

        // The message lists the codes of SINAN and of no other source. QQQQ is a made-up code.
        var ofSinan = "Of SINAN it has: " + string.Join(", ", Codes) + ". The catalog is partial. ";
        var listed = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get("SINAN", "INFL"));
        var madeUp = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get("sinan", "QQQQ"));
        var noSource = Assert.Throws<ArgumentException>(() => DatasusCatalog.Get("SINAN_P", "DENG"));
        Assert.StartsWith("The catalog has no dataset 'SINAN-INFL'. " + ofSinan, listed.Message, StringComparison.Ordinal);
        Assert.StartsWith("The catalog has no dataset 'sinan-QQQQ'. " + ofSinan, madeUp.Message, StringComparison.Ordinal);
        Assert.Equal("code", madeUp.ParamName);
        Assert.DoesNotContain("SIASUS", listed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RD, SP", listed.Message, StringComparison.Ordinal);
        Assert.StartsWith("The catalog has no source 'SINAN_P'. It has: SIASUS, SIHSUS, ", noSource.Message, StringComparison.Ordinal);
        Assert.Contains(", ESUSNOTIFICA, PO, SINAN. The catalog is partial. ", noSource.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("DENG,", noSource.Message, StringComparison.Ordinal);
    }

    private void Add(string directory, params (string Name, int Size)[] files)
    {
        foreach (var (name, size) in files)
        {
            _fixture.Server.Add(directory + "/" + name, new byte[size]);
        }
    }
}
