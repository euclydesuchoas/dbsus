using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// SIASUS-BI: a table of a large state is split into numbered parts (BIMG2312_1.dbc,
// BIMG2312_2.dbc), sometimes beside a file without a number (BIMG2312.dbc). Through the catalog,
// the numbered parts are the table and the file without a number is left aside. The file names
// are real ones, as the DATASUS server listed them in October 2026; their content is made up.
public sealed class DatasusNumberedPartsTests : IDisposable
{
    private const string Sia = "/SIASUS/200801_/Dados";
    private static readonly DatasusPeriod December = DatasusPeriod.OfMonth(2023, 12);
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasusDataset Bi => DatasusCatalog.SiaSus.BI;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Dataset_IsMonthly_ByState_InTheFolderOfSiasus_WithNumberedParts()
    {
        Assert.Equal(("SIASUS", "BI", "SIASUS-BI"), (Bi.Source, Bi.Code, Bi.ToString()));
        Assert.Equal("Produção Ambulatorial Individualizado", Bi.OfficialName);
        Assert.Equal(DatasusCoverage.Uf, Bi.Coverage);
        Assert.Equal(DatasusPeriodicity.Monthly, Bi.Periodicity);
        Assert.Equal(DatasusPeriod.OfMonth(2008, 1), Bi.FirstPeriod);
        Assert.Same(Bi, DatasusCatalog.Get("SIASUS", "BI"));

        var era = Assert.Single(Bi.Eras);
        Assert.Equal((Sia, "BI{UF}{YY}{MM}.dbc"), (era.Directory, era.FileNamePattern));
        Assert.Null(era.PreliminaryDirectory);
        Assert.True(era.NumberedParts);

        // The path is the name without a number, as the path of SIASUS-PA is the name without a letter.
        Assert.Equal(Sia + "/BIMG2312.dbc", Bi.GetPath(DatasusUf.MG, December));
        Assert.Equal([Sia + "/BIMG2312.dbc"], Bi.GetCandidatePaths(DatasusUf.MG, December));
        Assert.Equal(Sia + "/BIAP0801.dbc", Bi.GetPath(DatasusUf.AP, DatasusPeriod.OfMonth(2008, 1)));
        Assert.Throws<NotSupportedException>(() => Bi.GetNationalPath(December));

        // No other file type of the catalog has numbered parts.
        Assert.All(DatasusCatalog.Datasets.Where(dataset => dataset != Bi), dataset => Assert.All(dataset.Eras, other => Assert.False(other.NumberedParts)));
    }

    [Theory]
    [InlineData("BIMG2312.dbc", DatasusUf.MG, 2023, 12, null)]
    [InlineData("BIAP0801.dbc", DatasusUf.AP, 2008, 1, null)]
    [InlineData("BIMG2312_1.dbc", DatasusUf.MG, 2023, 12, 1)]
    [InlineData("BIMG2312_2.dbc", DatasusUf.MG, 2023, 12, 2)]
    [InlineData("BISP2502_3.dbc", DatasusUf.SP, 2025, 2, 3)]
    [InlineData("BIRJ2306_9.dbc", DatasusUf.RJ, 2023, 6, 9)]
    [InlineData("BIRJ2306_10.dbc", DatasusUf.RJ, 2023, 6, 10)]
    [InlineData("BIRJ2306_123.dbc", DatasusUf.RJ, 2023, 6, 123)]
    [InlineData("bimg2312_2.DBC", DatasusUf.MG, 2023, 12, 2)]
    public void TryParseNumberedFileName_ReadsTheStateThePeriodAndTheNumber(string fileName, DatasusUf uf, int year, int month, int? number)
    {
        Assert.True(Bi.CurrentEra.TryParseNumberedFileName(fileName, out var state, out var period, out var part));

        Assert.Equal((uf, DatasusPeriod.OfMonth(year, month), number), (state, period, part));
    }

    // A number is one or more digits, from 1 on, with no zero in front; nothing else is a part.
    [Theory]
    [InlineData("BIMG2312_0.dbc")]
    [InlineData("BIMG2312_01.dbc")]
    [InlineData("BIMG2312_.dbc")]
    [InlineData("BIMG2312_a.dbc")]
    [InlineData("BIMG2312a.dbc")]             // a letter, as the parts of SIASUS-PA have
    [InlineData("BIMG2312_1a.dbc")]
    [InlineData("BIMG2312_1_2.dbc")]
    [InlineData("BIMG2312-1.dbc")]
    [InlineData("BIMG2312 1.dbc")]
    [InlineData("BIMG23121.dbc")]
    [InlineData("BIMG231_1.dbc")]
    [InlineData("BIMG2313_1.dbc")]            // month 13
    [InlineData("BIXX2312_1.dbc")]            // not a state
    [InlineData("BIBR2312.dbc")]
    [InlineData("BIMG2312_1.dbf")]
    [InlineData("BIMG2312_1.zip")]
    [InlineData("BIMG2312_99999999999.dbc")]  // does not fit a number
    [InlineData("PAMG2312_1.dbc")]
    [InlineData("ABIMG2312_1.dbc")]
    [InlineData("BIMG2312_+1.dbc")]
    [InlineData("BIMG2312_１.dbc")]            // a digit that is not ASCII
    public void TryParseNumberedFileName_AnythingElse_IsNotAFileOfTheDataset(string fileName)
    {
        Assert.False(Bi.CurrentEra.TryParseNumberedFileName(fileName, out _, out _, out var number));
        Assert.Null(number);
    }

    // The number is never taken for the part letter of SIASUS-PA, nor the letter for a number.
    [Fact]
    public void NumbersAndLetters_AreNeverTakenForEachOther()
    {
        var bi = Bi.CurrentEra;
        var pa = DatasusCatalog.SiaSus.PA.CurrentEra;

        // SIASUS-PA and every other file type: a name with a number is not theirs.
        Assert.All(DatasusCatalog.Datasets.Where(dataset => dataset != Bi), dataset => Assert.All(dataset.Eras, era =>
        {
            Assert.False(era.TryParseFileName("PAMG2407_1.dbc", out _, out _, out _), dataset.ToString());
            Assert.False(era.TryParseFileName("BIMG2312_1.dbc", out _, out _, out _), dataset.ToString());
            Assert.False(era.TryParseFileName("BIMG2312.dbc", out _, out _, out _), dataset.ToString());
            Assert.False(era.TryParseNumberedFileName("BIMG2312_1.dbc", out _, out _, out _), dataset.ToString());
            Assert.False(era.TryParseNumberedFileName($"{dataset.Code}MG2312_1.dbc", out _, out _, out _), dataset.ToString());
        }));
        Assert.True(pa.TryParseFileName("PAMG2407a.dbc", out _, out _, out var letter));
        Assert.Equal('a', letter);

        // SIASUS-BI: a name with a letter is not its own, with either parser.
        Assert.False(bi.TryParseFileName("BIMG2312a.dbc", out _, out _, out _));
        Assert.False(bi.TryParseNumberedFileName("BIMG2312a.dbc", out _, out _, out _));
        Assert.False(bi.TryParseNumberedFileName("PAMG2407a.dbc", out _, out _, out _));
        Assert.False(bi.TryParseNumberedFileName("PAMG2407.dbc", out _, out _, out _));

        // The parser of the names with a letter reads the name of SIASUS-BI without a number, and no numbered one.
        Assert.True(bi.TryParseFileName("BIMG2312.dbc", out var uf, out var period, out var part));
        Assert.Equal((DatasusUf.MG, December, (char?)null), (uf, period, part));
        Assert.False(bi.TryParseFileName("BIMG2312_1.dbc", out _, out _, out _));
    }

    [Fact]
    public async Task ListTablesAsync_NumberedParts_AreOneTable_AndTheFileWithoutANumberIsLeftAside()
    {
        Add(Sia, ("BIMG2312_2.dbc", 20), ("BIMG2312_1.dbc", 300), ("BIMG2312.dbc", 310));              // the two parts and the file without a number
        Add(Sia, ("BIRJ2312_1.dbc", 5), ("BIRJ2312_2.dbc", 6), ("BIRJ2312_3.dbc", 7));                // only numbered parts
        Add(Sia, ("BIAP2312.dbc", 9));                                                                // only the file without a number
        Add(Sia, ("BIMG2311_1.dbc", 1), ("BISP2401_1.dbc", 1), ("BIMG2312a.dbc", 1), ("BIMG2312_0.dbc", 1), ("BIMG2312_x.dbc", 1));
        Add(Sia, ("PAMG2312a.dbc", 1), ("PAMG2312_1.dbc", 1), ("ADMG2312.dbc", 1), ("BIBR2312.dbc", 1), ("BIMG2312_1.zip", 1));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(Bi, December, December, cancellationToken: Ct);

        Assert.Equal(["AP", "MG", "RJ"], tables.Select(table => table.Uf.ToString()));
        Assert.Equal([Sia + "/BIAP2312.dbc", Sia + "/BIMG2312.dbc", Sia + "/BIRJ2312.dbc"], tables.Select(table => table.Path));
        Assert.Equal(["BIAP2312.dbc"], tables[0].Parts.Select(part => part.Name));
        Assert.Equal([Sia + "/BIMG2312_1.dbc", Sia + "/BIMG2312_2.dbc"], tables[1].Parts.Select(part => part.Path));
        Assert.Equal(320, tables[1].Size);
        Assert.Equal(["BIRJ2312_1.dbc", "BIRJ2312_2.dbc", "BIRJ2312_3.dbc"], tables[2].Parts.Select(part => part.Name));
        Assert.All(tables, table => Assert.False(table.IsPreliminary));

        // One listing, and no other request.
        Assert.Equal(1, _fixture.Server.ListCalls);
        Assert.Equal(0, _fixture.Server.InfoCalls);

        // The filter by state, and the other file types of the folder, which see no numbered file.
        var ofMinas = await client.ListTablesAsync(Bi, December, December, DatasusUf.MG, Ct);
        var outpatient = await client.ListTablesAsync(DatasusCatalog.SiaSus.PA, December, December, cancellationToken: Ct);
        Assert.Equal(["BIMG2312_1.dbc", "BIMG2312_2.dbc"], Assert.Single(ofMinas).Parts.Select(part => part.Name));
        Assert.Equal(["PAMG2312a.dbc"], Assert.Single(outpatient).Parts.Select(part => part.Name));
    }

    [Fact]
    public async Task ListTablesAsync_OrdersThePartsByNumber_NotByText_AndShowsWhatIsListedWhenANumberIsMissing()
    {
        Add(Sia, Enumerable.Range(1, 12).Reverse().Select(number => ($"BIMG2312_{number}.dbc", number)).ToArray());
        Add(Sia, ("BIRJ2312_1.dbc", 1), ("BIRJ2312_3.dbc", 3));
        var client = _fixture.Client();

        var tables = await client.ListTablesAsync(Bi, December, December, cancellationToken: Ct);

        Assert.Equal(Enumerable.Range(1, 12).Select(number => $"BIMG2312_{number}.dbc"), tables[0].Parts.Select(part => part.Name));
        Assert.Equal(Enumerable.Range(1, 12).Select(number => (long)number), tables[0].Parts.Select(part => part.Size));

        // A listing is shown as it is, as with the lettered parts; FindTableAsync is the one that refuses.
        Assert.Equal(["BIRJ2312_1.dbc", "BIRJ2312_3.dbc"], tables[1].Parts.Select(part => part.Name));
    }

    [Fact]
    public async Task FindTableAsync_NumberedParts_ReturnsThemInOrder_WithoutTheFileWithoutANumber()
    {
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", ClientFixture.Dbc("B1"), FakeServer.Time(2));
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", ClientFixture.Dbc("A1", "A2"), FakeServer.Time(1));
        _fixture.Server.Add(Sia + "/BIMG2312.dbc", ClientFixture.Dbc("X1", "X2", "X3"), FakeServer.Time(0));
        var client = _fixture.Client();

        var table = await client.FindTableAsync(Bi, DatasusUf.MG, December, Ct);

        Assert.Equal(Sia + "/BIMG2312.dbc", table.Path);
        Assert.Equal((Bi, DatasusUf.MG, December, false), (table.Dataset, table.Uf, table.Period, table.IsPreliminary));
        Assert.Equal([Sia + "/BIMG2312_1.dbc", Sia + "/BIMG2312_2.dbc"], table.Parts.Select(part => part.Path));
        Assert.Equal([FakeServer.Time(1), FakeServer.Time(2)], table.Parts.Select(part => part.LastModifiedUtc!.Value));
        Assert.Equal(table.Parts.Sum(part => part.Size), table.Size);

        // The two parts, and one question for a third: the file without a number is not asked for.
        Assert.Equal(1, _fixture.Server.ListCalls);
        Assert.Equal(3, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindTableAsync_OnlyTheFileWithoutANumber_IsTheTable()
    {
        _fixture.Server.Add(Sia + "/BIAP0801.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(4));
        var client = _fixture.Client();

        var table = await client.FindTableAsync(Bi, DatasusUf.AP, DatasusPeriod.OfMonth(2008, 1), Ct);

        Assert.Equal(Sia + "/BIAP0801.dbc", table.Path);
        var part = Assert.Single(table.Parts);
        Assert.Equal((Sia + "/BIAP0801.dbc", FakeServer.Time(4)), (part.Path, part.LastModifiedUtc));

        // The listing shows the file and no numbered part: one question, for the file itself.
        Assert.Equal(1, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindTableAsync_OrdersByNumber_AndAsksForThePartAfterTheLastOneListed()
    {
        foreach (var number in Enumerable.Range(1, 10))
        {
            _fixture.Server.Add(Sia + $"/BISP2502_{number}.dbc", ClientFixture.Dbc("P" + number));
        }

        var client = _fixture.Client();
        var february = DatasusPeriod.OfMonth(2025, 2);

        // The listing is taken, and then two more parts appear on the server.
        Assert.Equal(10, Assert.Single(await client.ListTablesAsync(Bi, february, february, cancellationToken: Ct)).Parts.Count);
        _fixture.Server.Add(Sia + "/BISP2502_11.dbc", ClientFixture.Dbc("P11"));
        _fixture.Server.Add(Sia + "/BISP2502_12.dbc", ClientFixture.Dbc("P12"));
        var table = await client.FindTableAsync(Bi, DatasusUf.SP, february, Ct);

        Assert.Equal(Enumerable.Range(1, 12).Select(number => $"BISP2502_{number}.dbc"), table.Parts.Select(part => part.Name));
        Assert.Equal(Sia + "/BISP2502.dbc", table.Path);
    }

    [Fact]
    public async Task FindTableAsync_NothingListed_AsksForTheFirstPartAndForTheFile()
    {
        _fixture.Server.Add(Sia + "/BIRJ2312_1.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        Assert.Single(await client.ListTablesAsync(Bi, December, December, cancellationToken: Ct));

        // After the listing: parts of another state, and a file without a number of a third.
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", ClientFixture.Dbc("B1"));
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", ClientFixture.Dbc("B2"));
        _fixture.Server.Add(Sia + "/BIES2312.dbc", ClientFixture.Dbc("C1"));
        var parts = await client.FindTableAsync(Bi, DatasusUf.MG, December, Ct);
        var file = await client.FindTableAsync(Bi, DatasusUf.ES, December, Ct);
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(Bi, DatasusUf.BA, December, Ct));

        Assert.Equal(["BIMG2312_1.dbc", "BIMG2312_2.dbc"], parts.Parts.Select(part => part.Name));
        Assert.Equal(["BIES2312.dbc"], file.Parts.Select(part => part.Name));
        Assert.Equal("The server has no SIASUS-BI table of BA for 2023-12: /SIASUS/200801_/Dados/BIBA2312.dbc does not exist, whole or in parts.", missing.Message);
    }

    [Fact]
    public async Task FindTableAsync_NumberMissingInTheMiddle_ThrowsNamingTheFileThatIsMissing()
    {
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Sia + "/BIMG2312_3.dbc", ClientFixture.Dbc("C1"));
        _fixture.Server.Add(Sia + "/BIMG2312.dbc", ClientFixture.Dbc("X1"));
        _fixture.Server.Add(Sia + "/BIRJ2312_2.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        var middle = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(Bi, DatasusUf.MG, December, Ct));
        var first = await Assert.ThrowsAsync<FileNotFoundException>(() => client.FindTableAsync(Bi, DatasusUf.RJ, December, Ct));
        var opening = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(Bi, DatasusUf.MG, December, cancellationToken: Ct));

        Assert.Equal("Part 2 of 'BIMG2312' is missing: 'BIMG2312_2.dbc' was not found.", middle.Message);
        Assert.Equal(Sia + "/BIMG2312_2.dbc", middle.FileName);
        Assert.Equal("Part 1 of 'BIRJ2312' is missing: 'BIRJ2312_1.dbc' was not found.", first.Message);
        Assert.Equal(middle.Message, opening.Message);

        // Nothing was downloaded, and the file without a number did not stand in for the parts.
        Assert.Equal(0, _fixture.Server.BytesServed);
    }

    // The choice of the parts among the names of a folder, without a server (the fake server of
    // these tests ignores the case of names, as the real one did in October 2026, so it cannot
    // list two names that differ only by case).
    [Fact]
    public void NumberedPartNaming_Select_OrdersByNumber_FindsTheFileWithoutANumber_AndRefusesGapsAndAmbiguousNames()
    {
        static Exception Missing(string name, string message) => new FileNotFoundException(message, name);
        string[] folder = ["BIMG2312_10.dbc", "BIMG2312_2.dbc", "bimg2312_1.DBC", "BIMG2312.dbc", "BIMG2312_3.dbc", "BIMG2312_4.dbc", "BIMG2312_5.dbc", "BIMG2312_6.dbc", "BIMG2312_7.dbc", "BIMG2312_8.dbc", "BIMG2312_9.dbc",
            "BIMG2312a.dbc", "BIMG2312_0.dbc", "BIMG2312_01.dbc", "BIMG2311_1.dbc", "BIRJ2312_1.dbc", "BIMG2312_1.zip", "PAMG2312_1.dbc"];

        var parts = NumberedPartNaming.Select(folder, "BIMG2312", ".dbc", out var unsplit, Missing);
        var none = NumberedPartNaming.Select(["BIAP0801.dbc", "BIAP0802_1.dbc"], "BIAP0801", ".dbc", out var alone, Missing);
        var neither = NumberedPartNaming.Select(["PAAP0801.dbc"], "BIAP0801", ".dbc", out var nothing, Missing);

        Assert.Equal(["bimg2312_1.DBC", "BIMG2312_2.dbc", "BIMG2312_3.dbc", "BIMG2312_4.dbc", "BIMG2312_5.dbc", "BIMG2312_6.dbc", "BIMG2312_7.dbc", "BIMG2312_8.dbc", "BIMG2312_9.dbc", "BIMG2312_10.dbc"], parts);
        Assert.Equal("BIMG2312.dbc", unsplit);
        Assert.Empty(none);
        Assert.Equal("BIAP0801.dbc", alone);
        Assert.Empty(neither);
        Assert.Null(nothing);

        var gap = Assert.Throws<FileNotFoundException>(() => NumberedPartNaming.Select(["BIMG2312_1.dbc", "BIMG2312_2.dbc", "BIMG2312_4.dbc"], "BIMG2312", ".dbc", out _, Missing));
        var noFirst = Assert.Throws<FileNotFoundException>(() => NumberedPartNaming.Select(["BIMG2312_2.dbc", "BIMG2312.dbc"], "BIMG2312", ".dbc", out _, Missing));
        var sameNumber = Assert.Throws<IOException>(() => NumberedPartNaming.Select(["BIMG2312_1.dbc", "bimg2312_1.DBC", "BIMG2312_2.dbc"], "BIMG2312", ".dbc", out _, Missing));
        var sameFile = Assert.Throws<IOException>(() => NumberedPartNaming.Select(["BIMG2312.dbc", "BIMG2312.DBC"], "BIMG2312", ".dbc", out _, Missing));
        Assert.Equal(("Part 3 of 'BIMG2312' is missing: 'BIMG2312_3.dbc' was not found.", "BIMG2312_3.dbc"), (gap.Message, gap.FileName));
        Assert.Equal("Part 1 of 'BIMG2312' is missing: 'BIMG2312_1.dbc' was not found.", noFirst.Message);
        Assert.Equal("Ambiguous part names that differ only by case: BIMG2312_1.dbc, bimg2312_1.DBC.", sameNumber.Message);
        Assert.Equal("Ambiguous part names that differ only by case: BIMG2312.dbc, BIMG2312.DBC.", sameFile.Message);
        Assert.Equal("BIMG2312_12.dbc", NumberedPartNaming.Name("BIMG2312", 12, ".dbc"));
    }

    [Fact]
    public async Task OpenReaderAsync_ReadsThePartsAsOneTable_InOrder_AndNeverDownloadsTheFileWithoutANumber()
    {
        var first = ClientFixture.Dbc("A1", "A2");
        var second = ClientFixture.Dbc("B1");
        var third = ClientFixture.Dbc("C1", "C2");
        _fixture.Server.Add(Sia + "/BIMG2312_3.dbc", third);
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", first);
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", second);
        _fixture.Server.Add(Sia + "/BIMG2312.dbc", ClientFixture.Dbc("X1", "X2", "X3", "X4", "X5", "X6", "X7"));
        var client = _fixture.Client();

        using (var reader = await client.OpenReaderAsync(Bi, DatasusUf.MG, December, cancellationToken: Ct))
        {
            Assert.Equal(3, reader.PartCount);
            Assert.Equal(["A1", "A2", "B1", "C1", "C2"], Codes(reader));
        }

        Assert.Equal(first.Length + second.Length + third.Length, _fixture.Server.BytesServed);

        // The cache: opening the table again asks for no byte.
        using (var reader = await client.OpenReaderAsync(Bi, DatasusUf.MG, December, cancellationToken: Ct))
        {
            Assert.Equal(["A1", "A2", "B1", "C1", "C2"], Codes(reader));
        }

        Assert.Equal(first.Length + second.Length + third.Length, _fixture.Server.BytesServed);

        // The table found, or listed, opens the same parts.
        var found = await client.FindTableAsync(Bi, DatasusUf.MG, December, Ct);
        var listed = Assert.Single(await client.ListTablesAsync(Bi, December, December, DatasusUf.MG, Ct));
        using (var reader = await client.OpenReaderAsync(found, cancellationToken: Ct))
        {
            Assert.Equal(["A1", "A2", "B1", "C1", "C2"], Codes(reader));
        }

        using (var reader = await client.OpenReaderAsync(listed, cancellationToken: Ct))
        {
            Assert.Equal(["A1", "A2", "B1", "C1", "C2"], Codes(reader));
        }

        Assert.Equal(first.Length + second.Length + third.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task OpenReaderAsync_OnlyTheFileWithoutANumber_ReadsIt()
    {
        _fixture.Server.Add(Sia + "/BIAP0801.dbc", ClientFixture.Dbc("A1", "A2", "A3"));
        var client = _fixture.Client();

        using var reader = await client.OpenReaderAsync(Bi, DatasusUf.AP, DatasusPeriod.OfMonth(2008, 1), cancellationToken: Ct);

        Assert.Equal(1, reader.PartCount);
        Assert.Equal(["A1", "A2", "A3"], Codes(reader));
    }

    // Parts of different sizes are one table; parts with different fields are not, and the error
    // is the one of the reader, which compares the headers of the parts.
    [Fact]
    public async Task OpenReaderAsync_PartsWithDifferentHeaders_FailWithTheErrorOfTheReader()
    {
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", SyntheticDbc.BuildDbc(new DbfBuilder().Field("CODE", 'C', 4).Field("QTY", 'N', 3).Record("A1", "  1").Record("A2", "  2").Build()));
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", SyntheticDbc.BuildDbc(new DbfBuilder().Field("CODE", 'C', 4).Record("B1").Build()));
        _fixture.Server.Add(Sia + "/BIRJ2312_1.dbc", ClientFixture.Dbc("A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8"));
        _fixture.Server.Add(Sia + "/BIRJ2312_2.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.OpenReaderAsync(Bi, DatasusUf.MG, December, cancellationToken: Ct));
        using var reader = await client.OpenReaderAsync(Bi, DatasusUf.RJ, December, cancellationToken: Ct);

        Assert.Contains("BIMG2312_2.dbc does not match BIMG2312_1.dbc", exception.Message, StringComparison.Ordinal);
        Assert.Equal(9, Codes(reader).Count);
    }

    [Fact]
    public async Task Cancellation_StopsTheSearchAndTheOpening()
    {
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.FindTableAsync(Bi, DatasusUf.MG, December, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListTablesAsync(Bi, December, December, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenReaderAsync(Bi, DatasusUf.MG, December, cancellationToken: cancelled.Token));
        Assert.Equal(0, _fixture.Server.BytesServed);

        // The client is still good.
        Assert.Equal(2, (await client.FindTableAsync(Bi, DatasusUf.MG, December, Ct)).Parts.Count);
    }

    // The rule is of the catalog. By path, the client follows the rules of DbSusFile.FindParts,
    // which knows lettered parts only: each of these names is a file of its own.
    [Fact]
    public async Task ByPath_ANumberedFileIsAFileOfItsOwn_AsInTheCore()
    {
        _fixture.Server.Add(Sia + "/BIMG2312_1.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/BIMG2312_2.dbc", ClientFixture.Dbc("B1"));
        _fixture.Server.Add(Sia + "/BIMG2312.dbc", ClientFixture.Dbc("X1", "X2", "X3"));
        var client = _fixture.Client();

        var whole = await client.FindPartsAsync(Sia + "/BIMG2312.dbc", Ct);
        var second = await client.FindPartsAsync(Sia + "/BIMG2312_2.dbc", Ct);

        Assert.Equal(["BIMG2312.dbc"], whole.Select(part => part.Name));
        Assert.Equal(["BIMG2312_2.dbc"], second.Select(part => part.Name));
        using (var reader = await client.OpenReaderAsync(Sia + "/BIMG2312_2.dbc", cancellationToken: Ct))
        {
            Assert.Equal(["B1"], Codes(reader));
        }

        using (var reader = await client.OpenReaderAsync(Sia + "/BIMG2312.dbc", cancellationToken: Ct))
        {
            Assert.Equal(["X1", "X2", "X3"], Codes(reader));
        }
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
