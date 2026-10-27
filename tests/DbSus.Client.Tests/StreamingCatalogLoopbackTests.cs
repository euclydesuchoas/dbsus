using DbSus.Client.Tests.Fakes;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// OpenStreamingReaderAsync by dataset, state and period, and OpenNationalStreamingReaderAsync,
// against a local FTP server, with the real transport: the records of the table form and of the
// forms that download, the same exception for a table that is not there, the limit of readers,
// and the token for the life of the reader.
public sealed class StreamingCatalogLoopbackTests : IDisposable
{
    private const string Root = "/dissemin/publicos";
    private const string Sia = "/SIASUS/200801_/Dados";
    private const string SimFinal = "/SIM/CID10/DORES";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    private static DatasusPeriod July2024 => DatasusPeriod.OfMonth(2024, 7);

    public void Dispose() => _server.Dispose();

    [Fact(Timeout = 60_000)]
    public async Task ByState_ReadsTheRecordsOfTheTableForm_AndOfOpenReaderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(Sia, ("PAMG2407a.dbc", Dbc(0, 3000)), ("PAMG2407b.dbc", Dbc(3000, 2000)));
        using var client = Client();

        List<string> streamed;
        using (var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct))
        {
            Assert.Equal(2, reader.PartCount);
            streamed = ReadAll(reader);
        }

        var table = await client.FindTableAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, ct);
        using (var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        using (var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        Assert.Equal(Enumerable.Range(0, 5000).Select(Code), streamed);
        Assert.Empty(_server.Restarts);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the readers to let go");
    }

    [Fact(Timeout = 60_000)]
    public async Task National_ReadsTheRecordsOfTheTableForm_AndOfOpenNationalReaderAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(SimFinal, ("DOBR2011.dbc", Dbc(0, 4000)));
        using var client = Client();

        List<string> streamed;
        using (var reader = await client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: ct))
        {
            streamed = ReadAll(reader);
        }

        var table = await client.FindNationalTableAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), ct);
        using (var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: ct))
        {
            Assert.Equal(ReadAll(reader), streamed);
        }

        Assert.Equal(Enumerable.Range(0, 4000).Select(Code), streamed);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the readers to let go");
    }

    // The exception of the forms that download, and no reader or place of the pool held after it.
    [Fact(Timeout = 60_000)]
    public async Task NoTable_ThrowsTheExceptionOfTheFormsThatDownload_AndHoldsNoPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(Sia, ("PAMG2407.dbc", Dbc(0, 10)));
        using var client = Client();

        var byState = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 8), cancellationToken: ct));
        var downloadByState = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 8), cancellationToken: ct));
        var national = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2030), cancellationToken: ct));
        var downloadNational = await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenNationalReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2030), cancellationToken: ct));

        Assert.Equal(downloadByState.Message, byState.Message);
        Assert.Equal(downloadByState.FileName, byState.FileName);
        Assert.Equal(downloadNational.Message, national.Message);
        Assert.Equal(downloadNational.FileName, national.FileName);
        Assert.Equal(0, client.OpenStreamingReaders);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.FreeConnectionPlaces == 2, "every place of the pool to be free");

        using var reader = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct);
        Assert.Equal(10, ReadAll(reader).Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task OneConnection_OrTheLimitReached_FailsAtOnce_WithNoConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        Serve(Sia, ("PAMG2407.dbc", Dbc(0, 10)));
        using (var single = Client(maxConnections: 1))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => single.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => single.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: ct));
            Assert.Equal(0, _server.ControlConnections);
            Assert.Equal(0, single.OpenStreamingReaders);
        }

        using var client = Client();
        using var first = await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct);

        // The first reader's transfer runs on its own: the counts are taken once it is quiet.
        for (var seen = -1; seen != _server.Commands.Count;)
        {
            seen = _server.Commands.Count;
            await Task.Delay(500, ct);
        }

        var connections = _server.ControlConnections;
        var commands = _server.Commands.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: ct));

        Assert.Equal(connections, _server.ControlConnections);
        Assert.Equal(commands, _server.Commands.Count);
        Assert.Equal(1, client.OpenStreamingReaders);
        Assert.Equal(10, ReadAll(first).Count);
    }

    // The token holds for the life of the reader: canceled while a read waits for data that does
    // not come, the read ends with OperationCanceledException.
    [Theory(Timeout = 60_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TokenCanceledWhileTheReadWaits_TheReadThrowsOperationCanceled(bool national)
    {
        var ct = TestContext.Current.CancellationToken;
        if (national)
        {
            Serve(SimFinal, ("DOBR2011.dbc", Dbc(0, 30_000)));
        }
        else
        {
            Serve(Sia, ("PAMG2407.dbc", Dbc(0, 30_000)));
        }

        _server.StallTransferAfter = 50_000;
        using var client = Client(readTimeout: TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = national
            ? await client.OpenNationalStreamingReaderAsync(DatasusCatalog.Sim.DO, DatasusPeriod.OfYear(2011), cancellationToken: cancellation.Token)
            : await client.OpenStreamingReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, July2024, cancellationToken: cancellation.Token);
        var records = 0;
        var reading = Task.Run(
            () =>
            {
                while (reader.Read())
                {
                    Interlocked.Increment(ref records);
                }
            },
            ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => Volatile.Read(ref records) > 0, "the first records");
        for (var seen = -1; seen != Volatile.Read(ref records);)
        {
            seen = Volatile.Read(ref records);
            await Task.Delay(300, ct);
        }

        Assert.False(reading.IsCompleted);

        cancellation.Cancel();

        var ended = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.Same(reading, ended);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.InRange(records, 1, 29_999);
        reader.Dispose();
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
    }

    private static string Code(int record) => (record % 10_000).ToString("0000", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] Dbc(int first, int count)
    {
        var builder = new DbfBuilder().Field("CODE", 'C', 4);
        for (var i = first; i < first + count; i++)
        {
            builder.Record(Code(i));
        }

        return SyntheticDbc.BuildDbc(builder.Build());
    }

    private static List<string> ReadAll(DbfReader reader)
    {
        var codes = new List<string>();
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }

        return codes;
    }

    // The files of one folder, and the listing that shows them (the server has one listing for
    // every folder).
    private void Serve(string folder, params (string Name, byte[] Content)[] files)
    {
        foreach (var (name, content) in files)
        {
            _server.Files[Root + folder + "/" + name] = content;
        }

        _server.Listing = [.. files.Select(file => LoopbackFtpServer.FileLine(file.Name, file.Content.Length))];
    }

    private DatasusClient Client(int maxConnections = 2, TimeSpan? readTimeout = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadTimeout = readTimeout ?? TimeSpan.FromSeconds(10),
        InitialRetryDelay = TimeSpan.Zero,
        MaxConnections = maxConnections,
        CacheDirectory = null,
    });
}
