using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Client.Tests;

// OpenStreamingReaderAsync by dataset, state and period, and OpenNationalStreamingReaderAsync, on the
// fake server: the final table that OpenReaderAsync and OpenNationalReaderAsync would open, the same
// exceptions, and the checks of the other forms of the streaming reader.
public sealed class StreamingCatalogTests : IDisposable
{
    private const string Sia = "/SIASUS/200801_/Dados";
    private const string SimFinal = "/SIM/CID10/DORES";
    private const string SimPreliminary = "/SIM/PRELIM/DORES";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DatasusPeriod July2024 => DatasusPeriod.OfMonth(2024, 7);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ByState_ReadsTheTableOfFindTableAsync_AndTheRecordsOfOpenReaderAsync()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/PAMG2407b.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client(cache: false);

        List<string> streamed;
        using (var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct))
        {
            Assert.Equal(2, reader.PartCount);
            streamed = ReadAll(reader);
        }

        var table = await client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, Ct);
        using (var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: Ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        Assert.Equal(["A1", "A2", "B1"], streamed);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the readers to let go");
    }

    [Fact]
    public async Task National_ReadsTheTableOfFindNationalTableAsync_AndTheRecordsOfOpenNationalReaderAsync()
    {
        _fixture.Server.Add(SimFinal + "/DOBR2011.dbc", ClientFixture.Dbc("F1", "F2"));
        var client = _fixture.Client(cache: false);

        List<string> streamed;
        using (var reader = await client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct))
        {
            streamed = ReadAll(reader);
        }

        var table = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), Ct);
        using (var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: Ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        Assert.Equal(["F1", "F2"], streamed);
    }

    [Fact]
    public async Task FinalAndPreliminaryExist_ReadsTheFinalOne()
    {
        _fixture.Server.Add(SimFinal + "/DORR2024.dbc", ClientFixture.Dbc("F1"));
        _fixture.Server.Add(SimPreliminary + "/DORR2024.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add(SimFinal + "/DOBR2024.dbc", ClientFixture.Dbc("F2"));
        _fixture.Server.Add(SimPreliminary + "/DOBR2024.dbc", ClientFixture.Dbc("P2"));
        var client = _fixture.Client(cache: false);

        using (var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2024), cancellationToken: Ct))
        {
            Assert.Equal(["F1"], ReadAll(reader));
        }

        using (var reader = await client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2024), cancellationToken: Ct))
        {
            Assert.Equal(["F2"], ReadAll(reader));
        }
    }

    // The message of OpenReaderAsync, with the form of the streaming reader that opens the
    // preliminary table; nothing is transferred, and no reader is left counted.
    [Fact]
    public async Task OnlyPreliminaryExists_ThrowsAsOpenReaderAsyncDoes_NamingTheStreamingForm()
    {
        _fixture.Server.Add(SimPreliminary + "/DORR2025.dbc", ClientFixture.Dbc("P1"));
        _fixture.Server.Add(SimPreliminary + "/DOBR2026.dbc", ClientFixture.Dbc("P2"));
        var client = _fixture.Client(cache: false);

        var byState = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), cancellationToken: Ct));
        var national = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2026), cancellationToken: Ct));
        var downloadByState = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025), cancellationToken: Ct));
        var downloadNational = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2026), cancellationToken: Ct));

        Assert.Equal(
            "SIM-DO of RR for 2025 is not published as final data: only the preliminary table /SIM/PRELIM/DORES/DORR2025.dbc exists, and this method opens final data only. "
            + "To read preliminary data, find the table with FindTableAsync and pass it to OpenStreamingReaderAsync(DatasusTableInfo).",
            byState.Message);
        Assert.Equal(
            "SIM-DO of the whole country for 2026 is not published as final data: only the preliminary table /SIM/PRELIM/DORES/DOBR2026.dbc exists, and this method opens final data only. "
            + "To read preliminary data, find the table with FindNationalTableAsync and pass it to OpenStreamingReaderAsync(DatasusTableInfo).",
            national.Message);
        Assert.Equal(downloadByState.Message.Replace("OpenReaderAsync(", "OpenStreamingReaderAsync("), byState.Message);
        Assert.Equal(downloadNational.Message.Replace("OpenReaderAsync(", "OpenStreamingReaderAsync("), national.Message);
        Assert.Equal(downloadByState.FileName, byState.FileName);
        Assert.Equal(downloadNational.FileName, national.FileName);
        Assert.Equal(0, _fixture.Server.Transfers);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task NoTable_ThrowsTheExceptionOfOpenReaderAsync_AndLetsTheReaderGo()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client(cache: false);

        var cases = new (Func<Task<DbfReader>> Streaming, Func<Task<DbfReader>> Download)[]
        {
            (() => client.OpenStreamingReaderAsync(DatasusCatalog.Sinasc.DN, DatasusUf.SP, DatasusPeriod.OfYear(2031), cancellationToken: Ct), () => client.OpenReaderAsync(DatasusCatalog.Sinasc.DN, DatasusUf.SP, DatasusPeriod.OfYear(2031), cancellationToken: Ct)),
            (() => client.OpenStreamingReaderAsync(DatasusCatalog.Cnes.LT, DatasusUf.SP, DatasusPeriod.OfMonth(2005, 8), cancellationToken: Ct), () => client.OpenReaderAsync(DatasusCatalog.Cnes.LT, DatasusUf.SP, DatasusPeriod.OfMonth(2005, 8), cancellationToken: Ct)),
            (() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2030), cancellationToken: Ct), () => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2030), cancellationToken: Ct)),
        };

        foreach (var (streaming, download) in cases)
        {
            var streamed = await Assert.ThrowsAsync<FileNotFoundException>(streaming);
            var downloaded = await Assert.ThrowsAsync<FileNotFoundException>(download);
            Assert.Equal(downloaded.Message, streamed.Message);
            Assert.Equal(downloaded.FileName, streamed.FileName);
            Assert.Equal(0, client.OpenStreamingReaders);
        }

        // The places were let go: with MaxConnections = 2, one reader opens.
        using var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct);
        Assert.Equal(["A1"], ReadAll(reader));
    }

    // The arguments are checked as OpenReaderAsync and OpenNationalReaderAsync check them, before
    // the limit of readers: with MaxConnections = 1 the same exception comes out, and nothing is sent.
    [Fact]
    public async Task WrongArguments_ThrowAsTheFormsThatDownload_BeforeTheLimitOfReaders()
    {
        var client = _fixture.Client(cache: false, configure: options => options.MaxConnections = 1);
        var sim = DatasusCatalog.Sim.DO;

        await Same<ArgumentNullException>(
            () => client.OpenStreamingReaderAsync((DatasusDataset)null!, DatasusUf.SP, July2024, cancellationToken: Ct),
            () => client.OpenReaderAsync((DatasusDataset)null!, DatasusUf.SP, July2024, cancellationToken: Ct));
        await Same<ArgumentException>(
            () => client.OpenStreamingReaderAsync(DatasusCatalog.Sim.DOFET, DatasusUf.SP, DatasusPeriod.OfYear(2020), cancellationToken: Ct),
            () => client.OpenReaderAsync(DatasusCatalog.Sim.DOFET, DatasusUf.SP, DatasusPeriod.OfYear(2020), cancellationToken: Ct));
        await Same<ArgumentOutOfRangeException>(
            () => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, default, July2024, cancellationToken: Ct),
            () => client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, default, July2024, cancellationToken: Ct));
        await Same<ArgumentException>(
            () => client.OpenStreamingReaderAsync(sim, DatasusUf.RR, July2024, cancellationToken: Ct),
            () => client.OpenReaderAsync(sim, DatasusUf.RR, July2024, cancellationToken: Ct));
        await Same<ArgumentNullException>(
            () => client.OpenNationalStreamingReaderAsync(null!, DatasusPeriod.OfYear(2011), cancellationToken: Ct),
            () => client.OpenNationalReaderAsync(null!, DatasusPeriod.OfYear(2011), cancellationToken: Ct));
        await Same<ArgumentException>(
            () => client.OpenNationalStreamingReaderAsync(DatasusCatalog.SiaSus.PA, July2024, cancellationToken: Ct),
            () => client.OpenNationalReaderAsync(DatasusCatalog.SiaSus.PA, July2024, cancellationToken: Ct));
        await Same<ArgumentException>(
            () => client.OpenNationalStreamingReaderAsync(sim, July2024, cancellationToken: Ct),
            () => client.OpenNationalReaderAsync(sim, July2024, cancellationToken: Ct));

        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task ClientDisposed_ThrowsObjectDisposed_BeforeAnythingElse()
    {
        var client = _fixture.Client(cache: false);
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenStreamingReaderAsync((DatasusDataset)null!, DatasusUf.MG, July2024, cancellationToken: Ct));
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
    }

    [Fact]
    public async Task OneConnection_IsRefusedAsByPath_BeforeAnythingIsSent()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(SimFinal + "/DOBR2011.dbc", ClientFixture.Dbc("F1"));
        var client = _fixture.Client(cache: false, configure: options => options.MaxConnections = 1);

        var byPath = await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenStreamingReaderAsync(Sia + "/PAMG2407a.dbc", cancellationToken: Ct));
        var byState = await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct));
        var national = await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct));

        Assert.Equal(byPath.Message, byState.Message);
        Assert.Equal(byPath.Message, national.Message);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
        Assert.Equal(0, _fixture.Server.ListCalls);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task LimitOfReadersReached_TheNewFormsFailAtOnce_WithNothingSent()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(SimFinal + "/DOBR2011.dbc", ClientFixture.Dbc("F1"));
        var client = _fixture.Client(cache: false);
        using var first = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct);
        var before = (_fixture.Server.ConnectionsOpened, _fixture.Server.Transfers, _fixture.Server.ListCalls);

        var byPath = client.OpenStreamingReaderAsync(Sia + "/PAMG2407a.dbc", cancellationToken: Ct);
        var byState = client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: Ct);
        var national = client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: Ct);

        Assert.True(byState.IsFaulted);
        Assert.True(national.IsFaulted);
        var expected = (await Assert.ThrowsAsync<InvalidOperationException>(() => byPath)).Message;
        Assert.Equal(expected, (await Assert.ThrowsAsync<InvalidOperationException>(() => byState)).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<InvalidOperationException>(() => national)).Message);
        Assert.Equal(before, (_fixture.Server.ConnectionsOpened, _fixture.Server.Transfers, _fixture.Server.ListCalls));
        Assert.Equal(1, client.OpenStreamingReaders);
        Assert.Equal(["A1"], ReadAll(first));
    }

    [Fact]
    public async Task CallerTokenCanceled_TheReadsThrowOperationCanceled()
    {
        _fixture.Server.Add(Sia + "/PAMG2407a.dbc", ClientFixture.Dbc("A1", "A2"));
        _fixture.Server.Add(Sia + "/PAMG2407b.dbc", ClientFixture.Dbc("B1"));
        var client = _fixture.Client(cache: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: cancellation.Token);
        Assert.True(reader.Read());

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ReadAll(reader));
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the canceled reader to let go");
    }

    [Fact]
    public async Task NationalCallerTokenCanceled_TheReadsThrowOperationCanceled()
    {
        _fixture.Server.Add(SimFinal + "/DOBR2011.dbc", ClientFixture.Dbc(Codes(20_000)));
        var client = _fixture.Client(cache: false);
        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(10);
        _fixture.Server.MaxChunk = 256;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var reader = await client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: cancellation.Token);
        Assert.True(reader.Read());

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ReadAll(reader));
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the canceled reader to let go");
    }

    // Every form of OpenStreamingReaderAsync, with its first argument only and with all of them,
    // binds to one overload: this test fails at compile time, not at run time.
    [Fact]
    public void EveryForm_CompilesWithItsFirstArgumentsOnly_AndWithAllOfThem()
    {
        var client = _fixture.Client(cache: false);
        DatasusTableInfo table = null!;
        DbfReadOptions? dbf = null;
        DbSus.Dbc.DbcReadOptions? dbc = null;
        var ct = CancellationToken.None;

        Func<Task<DbfReader>>[] calls =
        [
            () => client.OpenStreamingReaderAsync("/X/PAMG2407.dbc"),
            () => client.OpenStreamingReaderAsync("/X/PAMG2407.dbc", dbf, dbc, ct),
            () => client.OpenStreamingReaderAsync(table),
            () => client.OpenStreamingReaderAsync(table, dbf, dbc, ct),
            () => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024),
            () => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, dbf, dbc, ct),
            () => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011)),
            () => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), dbf, dbc, ct),
            () => client.OpenStreamingReaderAsync("/X/PAMG2407.dbc", cancellationToken: ct),
            () => client.OpenStreamingReaderAsync(table, cancellationToken: ct),
            () => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct),
            () => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: ct),
        ];

        Assert.Equal(12, calls.Length);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
    }

    private static string[] Codes(int count) => [.. Enumerable.Range(0, count).Select(i => (i % 10_000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture))];

    private static List<string> ReadAll(DbfReader reader)
    {
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    private static async Task Same<TException>(Func<Task<DbfReader>> streaming, Func<Task<DbfReader>> download)
        where TException : Exception
    {
        var expected = await Assert.ThrowsAsync<TException>(download);
        var actual = await Assert.ThrowsAsync<TException>(streaming);
        Assert.Equal(expected.Message, actual.Message);
    }
}
