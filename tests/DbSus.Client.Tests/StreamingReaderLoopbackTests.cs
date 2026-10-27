using DbSus.Client.Tests.Fakes;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// The reader from OpenStreamingReaderAsync against a local FTP server, with the real transport:
// disposing it from another thread while a read waits for data that does not come.
public sealed class StreamingReaderLoopbackTests : IDisposable
{
    private const string FilePath = "/X/PAMG2407.dbc";
    private const string FullPath = "/dissemin/publicos/X/PAMG2407.dbc";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };

    public void Dispose() => _server.Dispose();

    // The time is what the server's MDTM answers, so that the versions match.
    private static readonly DateTime Modified = new(2025, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact(Timeout = 60_000)]
    public async Task Parts_FollowOneAnotherOnTheConnectionOfTheBody()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Dbc(0, 3000);
        var b = Dbc(3000, 2000);
        _server.Files["/dissemin/publicos/X/PAMG2407a.dbc"] = a;
        _server.Files["/dissemin/publicos/X/PAMG2407b.dbc"] = b;
        using var client = Client(TimeSpan.FromSeconds(10));
        var table = new DatasusTableInfo(
            DatasusCatalog.SiaSus.PA,
            DatasusUf.MG,
            DatasusPeriod.OfMonth(2024, 7),
            isPreliminary: false,
            FilePath,
            [new DatasusFileInfo("/X/PAMG2407a.dbc", "PAMG2407a.dbc", a.Length, Modified, isDirectory: false), new DatasusFileInfo("/X/PAMG2407b.dbc", "PAMG2407b.dbc", b.Length, Modified, isDirectory: false)]);

        using var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: ct);
        var records = 0;
        while (reader.Read())
        {
            Assert.Equal(Code(records), reader.GetString(0));
            records++;
        }

        // One connection reads part b's header ahead and is closed by force in the middle of the
        // file; part a's body is read on a new one, and part b's after it, on the same.
        Assert.Equal(5000, records);
        Assert.Equal(2, _server.ControlConnections);
        Assert.Equal(3, _server.Commands.Count(command => command == "RETR"));
        Assert.Empty(_server.Restarts);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
    }

    [Fact(Timeout = 60_000)]
    public async Task ListingTimeToTheMinute_TheVersionIsTheOneOfMdtm_AndNoChangeIsSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Dbc(0, 300);
        var b = Dbc(300, 200);
        _server.Files["/dissemin/publicos/X/PAMG2407a.dbc"] = a;
        _server.Files["/dissemin/publicos/X/PAMG2407b.dbc"] = b;

        // LIST says 12:00, to the minute; MDTM says 12:07:33.
        _server.Listing = [LoopbackFtpServer.FileLine("PAMG2407a.dbc", a.Length), LoopbackFtpServer.FileLine("PAMG2407b.dbc", b.Length)];
        _server.MdtmReply = "213 20250808120733";
        using var client = Client(TimeSpan.FromSeconds(10));

        var listed = await client.ListAsync("/X", ct);
        var parts = await client.FindPartsAsync(FilePath, ct);
        using var reader = await client.OpenStreamingReaderAsync(FilePath, cancellationToken: ct);
        var records = 0;
        while (reader.Read())
        {
            Assert.Equal(Code(records), reader.GetString(0));
            records++;
        }

        Assert.All(listed, entry => Assert.Null(entry.LastModifiedUtc));
        Assert.All(parts, part => Assert.Equal(new DateTime(2025, 8, 8, 12, 7, 33, DateTimeKind.Utc), part.LastModifiedUtc));
        Assert.Equal(500, records);
    }

    // The transfer of part b's header, read ahead, ends inside the 69 bytes it takes (the header and
    // its checksum): in the first 32, in the rest of the header, or in the checksum. Part b is longer,
    // so the transfer was cut short: it is read again on the reader's place, and the opening goes on.
    [Theory(Timeout = 60_000)]
    [InlineData(20, "426 Connection closed; transfer aborted.")]
    [InlineData(50, "226 Transfer complete.")]
    [InlineData(67, "426 Connection closed; transfer aborted.")]
    public async Task HeaderReadAheadEndsInsideIt_ItIsReadAgain_AndTheOpeningGoesOn(long cut, string reply)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Dbc(0, 300);
        var b = Dbc(300, 200);
        _server.Files["/dissemin/publicos/X/PAMG2407a.dbc"] = a;
        _server.Files["/dissemin/publicos/X/PAMG2407b.dbc"] = b;
        _server.EndTransferAfter = cut;
        _server.EndedTransferReply = reply;
        var retries = 0;
        using var client = Client(TimeSpan.FromSeconds(10), onRetry: () =>
        {
            Interlocked.Increment(ref retries);
            _server.EndTransferAfter = null;
        });

        using var reader = await client.OpenStreamingReaderAsync(Table(a.Length, b.Length), cancellationToken: ct);
        var records = 0;
        while (reader.Read())
        {
            Assert.Equal(Code(records), reader.GetString(0));
            records++;
        }

        // Part b's header twice, then part a's body and part b's.
        Assert.Equal(500, records);
        Assert.Equal(1, Volatile.Read(ref retries));
        Assert.Equal(4, _server.Commands.Count(command => command == "RETR"));
        Assert.Empty(_server.Restarts);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
    }

    // A part b that really ends inside the bytes its header takes is invalid, and is not read again.
    [Theory(Timeout = 60_000)]
    [InlineData(20)]
    [InlineData(67)]
    public async Task PartEndsInsideItsHeader_TheOpeningThrowsInvalidData_WithoutReadingItAgain(int length)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = Dbc(0, 300);
        byte[] b = [.. Dbc(300, 200).Take(length)];
        _server.Files["/dissemin/publicos/X/PAMG2407a.dbc"] = a;
        _server.Files["/dissemin/publicos/X/PAMG2407b.dbc"] = b;
        var retries = 0;
        using var client = Client(TimeSpan.FromSeconds(10), onRetry: () => Interlocked.Increment(ref retries));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.OpenStreamingReaderAsync(Table(a.Length, b.Length), cancellationToken: ct));

        Assert.Contains("ends inside its header", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref retries));
        Assert.Single(_server.Commands, command => command == "RETR");
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact(Timeout = 60_000)]
    public async Task DisposedFromAnotherThread_WhileTheReadWaitsForData_TheReadEndsWithObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Dbc(0, 30_000);
        _server.Files[FullPath] = content;
        _server.StallTransferAfter = 50_000;
        using var client = Client(TimeSpan.FromSeconds(60));
        var part = new DatasusFileInfo(FilePath, "PAMG2407.dbc", content.Length, Modified, isDirectory: false);
        var table = new DatasusTableInfo(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), isPreliminary: false, FilePath, [part]);
        var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: ct);
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

        // Every record of the 50,000 bytes is read, and the read waits for the rest.
        await ReadAheadStreamTests.WaitUntilAsync(() => Volatile.Read(ref records) > 0, "the first records");
        for (var seen = -1; seen != Volatile.Read(ref records);)
        {
            seen = Volatile.Read(ref records);
            await Task.Delay(300, ct);
        }

        Assert.False(reading.IsCompleted);

        reader.Dispose();

        var ended = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.Same(reading, ended);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reading);
        Assert.InRange(records, 1, 29_999);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
        Assert.Equal(1, _server.Commands.Count(command => command == "RETR"));
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

    // The table of parts a and b, with the time the server's MDTM answers.
    private static DatasusTableInfo Table(long sizeA, long sizeB) => new(
        DatasusCatalog.SiaSus.PA,
        DatasusUf.MG,
        DatasusPeriod.OfMonth(2024, 7),
        isPreliminary: false,
        FilePath,
        [new DatasusFileInfo("/X/PAMG2407a.dbc", "PAMG2407a.dbc", sizeA, Modified, isDirectory: false), new DatasusFileInfo("/X/PAMG2407b.dbc", "PAMG2407b.dbc", sizeB, Modified, isDirectory: false)]);

    private DatasusClient Client(TimeSpan readTimeout, Action? onRetry = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReadTimeout = readTimeout,
        InitialRetryDelay = TimeSpan.Zero,
        CacheDirectory = null,
        OnRetry = onRetry is null ? null : _ => onRetry(),
    });
}
