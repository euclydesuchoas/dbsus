using System.Collections.Concurrent;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// The request of one call (DatasusFileRequest), and the progress of that call alone. The fixture's
// clock stands still, so unless a test gives another one, a stage reports only its start and its end.
public sealed class ProgressTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private const string Single = Dados + "/RDMG2407.dbc";
    private const string Split = Dados + "/PAMG2407.dbc";
    private const string PartA = Dados + "/PAMG2407a.dbc";
    private const string PartB = Dados + "/PAMG2407b.dbc";
    private const DatasusProgressStage Downloading = DatasusProgressStage.Downloading;
    private const DatasusProgressStage Verifying = DatasusProgressStage.Verifying;
    private const DatasusProgressStage FromCache = DatasusProgressStage.FromCache;
    private const DatasusProgressStage Streaming = DatasusProgressStage.Streaming;
    private static readonly byte[] Content = ClientFixture.Dbc(Codes('R', 9000));
    private readonly ClientFixture _fixture = new();
    private readonly Recorder _progress = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static long Size => Content.Length;

    public void Dispose()
    {
        _fixture.Server.ReleaseHeld();
        _fixture.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WholeDownload_ReportsTheStartAndTheEndOfTheTransferAndOfTheCheck(bool cache)
    {
        _fixture.Server.Add(Single, Content);
        var client = _fixture.Client(cache);

        var path = await client.DownloadAsync(Request(Single), Ct);

        Assert.Equal(Content, File.ReadAllBytes(path));
        Assert.Equal(
            [(Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L), (Verifying, 0L, Size, 0L), (Verifying, Size, Size, 0L)],
            _progress.Shape);
        Assert.All(_progress.Reports, report => Assert.Equal(Single, report.RemotePath));
        Assert.Equal("Downloading " + Single, _progress.Reports[0].Operation);
        Assert.Equal("Verifying " + Single, _progress.Reports[2].Operation);
    }

    [Fact]
    public async Task VerifyOnDownloadOff_ReportsNoCheck()
    {
        _fixture.Server.Add(Single, Content);

        await _fixture.Client(configure: options => options.VerifyOnDownload = false).DownloadAsync(Request(Single), Ct);

        Assert.Equal([(Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L)], _progress.Shape);
    }

    [Fact]
    public async Task FileThatIsNotADbc_ReportsNoCheck()
    {
        const string Path = Dados + "/README.txt";
        _fixture.Server.Add(Path, [1, 2, 3]);

        await _fixture.Client().DownloadAsync(Request(Path), Ct);

        Assert.Equal([(Downloading, 0L, 3L, 0L), (Downloading, 3L, 3L, 0L)], _progress.Shape);
    }

    [Fact]
    public async Task TransferThatDrops_ReportsWhereTheNextOneResumed()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.FailNextTransferAfter(4000);

        await _fixture.Client().DownloadAsync(Request(Single), Ct);

        Assert.Equal([0L, 4000L], _fixture.Server.TransferOffsets);
        Assert.Equal(
            [(Downloading, 0L, Size, 0L), (Downloading, 4000L, Size, 4000L), (Downloading, Size, Size, 4000L)],
            _progress.Shape.Where(report => report.Stage == Downloading));
    }

    [Fact]
    public async Task PartialDownloadOfAnEarlierCall_ReportsWhereItResumed()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.FailNextTransferAfter(4000);
        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => _fixture.Client(configure: options => options.MaxRetries = 0).DownloadAsync(Single, Ct));

        await _fixture.Client().DownloadAsync(Request(Single), Ct);

        Assert.Equal(
            [(Downloading, 4000L, Size, 4000L), (Downloading, Size, Size, 4000L)],
            _progress.Shape.Where(report => report.Stage == Downloading));
    }

    [Fact]
    public async Task ResumptionThatFailsToOpen_StartsOver_ReportsNothingForIt_AndGoesBackToZero()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.FailNextTransferAfter(3000);
        _fixture.Server.ResumptionsThatTimeOut = 1;

        await _fixture.Client().DownloadAsync(Request(Single), Ct);

        Assert.Equal([0L, 3000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal(
            [(Downloading, 0L, Size, 0L), (Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L)],
            _progress.Shape.Where(report => report.Stage == Downloading));
    }

    [Fact]
    public async Task FileThatChangesOnTheServer_GoesBackToZero_WithTheSizeOfTheNewVersion()
    {
        var smaller = ClientFixture.Dbc(Codes('S', 100));
        _fixture.Server.Add(Single, Content);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 1)
            {
                _fixture.Server.Add(Single, smaller, FakeServer.Time(5));
            }
        };

        var path = await _fixture.Client().DownloadAsync(Request(Single), Ct);

        Assert.Equal(smaller, File.ReadAllBytes(path));
        Assert.Equal(
            [(Downloading, 0L, Size, 0L), (Downloading, 0L, (long)smaller.Length, 0L), (Downloading, (long)smaller.Length, (long)smaller.Length, 0L)],
            _progress.Shape.Where(report => report.Stage == Downloading));
    }

    [Fact]
    public async Task CheckThatFails_DownloadsAgain_AndOnlyTheCheckThatPassesReportsItsEnd()
    {
        var corrupt = ClientFixture.CorruptDbc(Codes('R', 9000));
        Assert.Equal(Size, corrupt.Length);
        _fixture.Server.Add(Single, corrupt);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(Single, Content);
            }
        };

        await _fixture.Client().DownloadAsync(Request(Single), Ct);

        Assert.Equal(
            [
                (Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L), (Verifying, 0L, Size, 0L),
                (Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L), (Verifying, 0L, Size, 0L), (Verifying, Size, Size, 0L),
            ],
            _progress.Shape);
    }

    [Fact]
    public async Task FileFromTheCache_ReportsOnceWithEveryByteDone()
    {
        _fixture.Server.Add(Single, Content);
        var client = _fixture.Client();
        await client.DownloadAsync(Single, Ct);

        await client.DownloadAsync(Request(Single), Ct);
        using (await client.OpenReaderAsync(Request(Single), Ct))
        {
        }

        Assert.Equal([(FromCache, Size, Size, 0L), (FromCache, Size, Size, 0L)], _progress.Shape);
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.Equal($"Reading {Single} from the cache", _progress.Reports[0].Operation);
    }

    // The second call waits for the entry the first is downloading, and is told nothing until then.
    [Fact(Timeout = 60_000)]
    public async Task TwoCallsForTheSameFile_TheOneThatWaitsReceivesOnlyFromCacheAtTheEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        var client = _fixture.Client();
        var first = new Recorder();
        _fixture.Server.Hold = (_, _) => true;
        var downloading = Task.Run(() => client.DownloadAsync(new DatasusFileRequest(Single) { Progress = first }, ct), ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.Held == 1, "the first call's transfer to start");

        var waiting = client.DownloadAsync(Request(Single), ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.InfoCalls == 2, "the second call to ask the size and time");
        Assert.Empty(_progress.Shape);

        _fixture.Server.ReleaseHeld();
        await downloading;
        await waiting;

        Assert.Contains((Downloading, Size, Size, 0L), first.Shape);
        Assert.Equal([(FromCache, Size, Size, 0L)], _progress.Shape);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoCallsForTheSameFile_WhenTheFirstFails_TheOneThatWaitedDownloadsAndReportsThat()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        var client = _fixture.Client(configure: options => options.MaxRetries = 0);
        _fixture.Server.Hold = (_, _) => true;
        var failing = Task.Run(() => client.DownloadAsync(Single, ct), ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.Held == 1, "the first call's transfer to start");

        var waiting = client.DownloadAsync(Request(Single), ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.InfoCalls == 2, "the second call to ask the size and time");
        _fixture.Server.FailNextTransferAfter(4000);
        _fixture.Server.ReleaseHeld();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => failing);
        await waiting;

        Assert.Equal([(Downloading, 4000L, Size, 4000L), (Downloading, Size, Size, 4000L)], _progress.Shape.Where(report => report.Stage == Downloading));
        Assert.DoesNotContain(_progress.Shape, report => report.Stage == FromCache);
    }

    [Fact]
    public async Task TableOfSeveralParts_ReportsPartByPart_WithTheSizeOfEachPart()
    {
        var a = ClientFixture.Dbc(Codes('A', 300));
        var b = ClientFixture.Dbc(Codes('B', 200));
        _fixture.Server.Add(PartA, a);
        _fixture.Server.Add(PartB, b);
        var client = _fixture.Client(configure: options => options.VerifyOnDownload = false);

        using var reader = await client.OpenReaderAsync(Request(Split), Ct);

        Assert.Equal(
            [
                (PartA, 0L, (long)a.Length), (PartA, (long)a.Length, (long)a.Length),
                (PartB, 0L, (long)b.Length), (PartB, (long)b.Length, (long)b.Length),
            ],
            _progress.Reports.Select(report => (report.RemotePath, report.BytesDone, report.TotalBytes)));
    }

    [Fact(Timeout = 60_000)]
    public async Task Streaming_ReportsOnThreadsOfThePool_ButNeverTheReadingThread()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        using var reader = await _fixture.Client().OpenStreamingReaderAsync(Request(Single), ct);

        // A thread of its own, which is not one of the pool.
        var records = 0;
        var reading = new Thread(() =>
        {
            while (reader.Read())
            {
                records++;
            }
        });
        reading.Start();
        reading.Join();

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Shape.Contains((Streaming, Size, Size, 0L)), "the last report");
        Assert.Equal(9000, records);
        Assert.Equal([(Streaming, 0L, Size, 0L), (Streaming, Size, Size, 0L)], _progress.Shape);
        Assert.Equal("Streaming " + Single, _progress.Reports[0].Operation);
        Assert.All(_progress.OnThreadOfThePool, Assert.True);
    }

    // The file fits the reader's buffer: all of it is received while the consumer holds one record.
    [Fact(Timeout = 60_000)]
    public async Task Streaming_CountsTheBytesReceived_NotTheOnesTheReaderConsumed()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(Size < RemoteReadAheadStream.Capacity);
        _fixture.Server.Add(Single, Content);
        using var reader = await _fixture.Client().OpenStreamingReaderAsync(Request(Single), ct);

        Assert.True(reader.Read());

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Shape.Contains((Streaming, Size, Size, 0L)), "the last byte to be received");
    }

    [Fact(Timeout = 60_000)]
    public async Task Streaming_TransferThatDrops_ReportsWhereTheNextOneResumed()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        _fixture.Server.FailNextTransferAfter(2000);
        using var reader = await _fixture.Client().OpenStreamingReaderAsync(Request(Single), ct);

        ReadToTheEnd(reader);

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Shape.Contains((Streaming, Size, Size, 2000L)), "the last report");
        Assert.Equal([0L, 2000L], _fixture.Server.TransferOffsets);
        Assert.Equal([(Streaming, 0L, Size, 0L), (Streaming, 2000L, Size, 2000L), (Streaming, Size, Size, 2000L)], _progress.Shape);
    }

    // Starting over from byte 0, the bytes read again are dropped: BytesDone never goes back.
    [Fact(Timeout = 60_000)]
    public async Task Streaming_ResumptionThatFailsToOpen_StartsOver_AndTheBytesDoneDoNotGoBack()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        _fixture.Server.FailNextTransferAfter(3000);
        _fixture.Server.ResumptionsThatTimeOut = 1;
        using var reader = await _fixture.Client().OpenStreamingReaderAsync(Request(Single), ct);

        ReadToTheEnd(reader);

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Shape.Contains((Streaming, Size, Size, 0L)), "the last report");
        Assert.Equal([0L, 3000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal([(Streaming, 0L, Size, 0L), (Streaming, 3000L, Size, 0L), (Streaming, Size, Size, 0L)], _progress.Shape);
    }

    // A later part starts when the reader gets to it, past the header it read ahead at the opening.
    [Fact(Timeout = 60_000)]
    public async Task Streaming_TableOfSeveralParts_ReportsPartByPart_InOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = ClientFixture.Dbc(Codes('A', 300));
        var b = ClientFixture.Dbc(Codes('B', 200));
        var ahead = (b[8] | (b[9] << 8)) + 4L;
        _fixture.Server.Add(PartA, a);
        _fixture.Server.Add(PartB, b);
        using var reader = await _fixture.Client().OpenStreamingReaderAsync(Request(Split), ct);

        Assert.Equal(300 + 200, ReadToTheEnd(reader));

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Reports.Any(report => report.RemotePath == PartB && report.BytesDone == b.Length), "the last report");
        Assert.Equal(
            [
                (PartA, 0L, (long)a.Length, 0L), (PartA, (long)a.Length, (long)a.Length, 0L),
                (PartB, ahead, (long)b.Length, 0L), (PartB, (long)b.Length, (long)b.Length, 0L),
            ],
            _progress.Reports.Select(report => (report.RemotePath, report.BytesDone, report.TotalBytes, report.ResumedAt)));
    }

    [Fact(Timeout = 60_000)]
    public async Task CallbackThatThrows_ChangesNothing_InADownload_InTheCheck_FromTheCache_AndReadingWithoutADownload()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        var client = _fixture.Client();
        var calls = 0;
        var throwing = new Recorder
        {
            Then = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("The caller's callback failed.");
            },
        };
        var request = new DatasusFileRequest(Single) { Progress = throwing };

        Assert.Equal(Content, File.ReadAllBytes(await client.DownloadAsync(request, ct)));
        Assert.Equal(4, Volatile.Read(ref calls));
        Assert.Equal(Content, File.ReadAllBytes(await client.DownloadAsync(request, ct)));
        Assert.Equal(5, Volatile.Read(ref calls));

        using var reader = await client.OpenStreamingReaderAsync(request, ct);
        Assert.Equal(9000, ReadToTheEnd(reader));
        await ReadAheadStreamTests.WaitUntilAsync(() => Volatile.Read(ref calls) == 7, "the two reports of the reader");
    }

    [Fact(Timeout = 60_000)]
    public async Task CallbackThatCancels_TheCallEndsCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _progress.Then = _ => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Client().DownloadAsync(Request(Single), cancel.Token));

        Assert.Equal([(Downloading, 0L, Size, 0L)], _progress.Shape);
    }

    // The clock stands still: however many reads the transfer takes, only its start and its end.
    [Fact]
    public async Task Frequency_NoTimeGoesBy_OnlyTheStartAndTheEnd()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.MaxChunk = 256;

        await _fixture.Client(configure: options => options.VerifyOnDownload = false).DownloadAsync(Request(Single), Ct);

        Assert.True(Size > 256 * 4);
        Assert.Equal([(Downloading, 0L, Size, 0L), (Downloading, Size, Size, 0L)], _progress.Shape);
    }

    // Each read of 256 bytes takes 100 ms of the clock: a report every three reads, 300 ms apart,
    // never 250 ms or less; and the last byte is reported whenever it comes.
    [Fact]
    public async Task Frequency_AtMostOnceEvery250Milliseconds_HoweverFastTheLink_AndTheEndAlways()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.MaxChunk = 256;
        var now = _fixture.Now;

        await _fixture.Client(configure: options => options.VerifyOnDownload = false, clock: () => now = now.AddMilliseconds(100)).DownloadAsync(Request(Single), Ct);

        var done = _progress.Reports.Select(report => report.BytesDone).ToList();
        Assert.Equal(0L, done[0]);
        Assert.Equal(Size, done[done.Count - 1]);
        Assert.True(done.Count > 3, done.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 1; i < done.Count - 1; i++)
        {
            Assert.Equal(3 * 256, done[i] - done[i - 1]);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Frequency_ReadingWithoutADownload_AtMostOnceEvery250Milliseconds()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Content);
        _fixture.Server.MaxChunk = 256;
        var ticks = _fixture.Now.Ticks;
        using var reader = await _fixture.Client(clock: () => new DateTime(Interlocked.Add(ref ticks, TimeSpan.FromMilliseconds(100).Ticks), DateTimeKind.Utc)).OpenStreamingReaderAsync(Request(Single), ct);

        ReadToTheEnd(reader);

        await ReadAheadStreamTests.WaitUntilAsync(() => _progress.Shape.Contains((Streaming, Size, Size, 0L)), "the last report");
        var done = _progress.Reports.Select(report => report.BytesDone).ToList();
        Assert.True(done.Count > 3, done.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var i = 1; i < done.Count - 1; i++)
        {
            Assert.Equal(3 * 256, done[i] - done[i - 1]);
        }
    }

    [Fact]
    public async Task Frequency_AClockSetBack_DoesNotStopTheReports()
    {
        _fixture.Server.Add(Single, Content);
        _fixture.Server.MaxChunk = 4096;
        var now = _fixture.Now;

        await _fixture.Client(configure: options => options.VerifyOnDownload = false, clock: () => now = now.AddHours(-1)).DownloadAsync(Request(Single), Ct);

        Assert.True(_progress.Reports.Count > 2, _progress.Reports.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task OpenReaderAsync_ByPathAndByTable_ReadTheSameRecords_WithTheirOptions_AndNothingElseChanges()
    {
        _fixture.Server.Add(Single, Table());
        var client = _fixture.Client();
        var month = DatasusPeriod.OfMonth(2024, 7);
        var table = new DatasusTableInfo(DatasusCatalog.SihSus.RD, DatasusUf.MG, month, isPreliminary: false, Single, await client.FindPartsAsync(Single, Ct));
        var options = new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.Null };

        using (var reader = await client.OpenReaderAsync(new DatasusFileRequest(Single) { DbfOptions = options }, Ct))
        {
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(1));
        }

        using (var reader = await client.OpenReaderAsync(new DatasusFileRequest(table) { DbfOptions = options }, Ct))
        {
            Assert.True(reader.Read());
            Assert.True(reader.IsDBNull(1));
        }

        // No progress and no options: as the overload with a path alone.
        using (var reader = await client.OpenReaderAsync(new DatasusFileRequest(Single), Ct))
        {
            Assert.True(reader.Read());
            Assert.Throws<FormatException>(() => reader.GetValue(1));
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task OpenStreamingReaderAsync_ByPathAndByTable_WithTheirOptions_AndTheirPlaceAmongTheReaders()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(Single, Table());
        var client = _fixture.Client();
        var table = new DatasusTableInfo(DatasusCatalog.SihSus.RD, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), isPreliminary: false, Single, await client.FindPartsAsync(Single, ct));
        var options = new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.Null };

        foreach (var request in new[] { new DatasusFileRequest(Single) { DbfOptions = options }, new DatasusFileRequest(table) { DbfOptions = options } })
        {
            using (var reader = await client.OpenStreamingReaderAsync(request, ct))
            {
                Assert.Equal(1, client.OpenStreamingReaders);
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenStreamingReaderAsync(request, ct));
                Assert.True(reader.Read());
                Assert.True(reader.IsDBNull(1));
            }

            Assert.Equal(0, client.OpenStreamingReaders);
        }
    }

    [Fact]
    public async Task DbcOptions_ReachTheReader_InBothWaysOfOpening()
    {
        _fixture.Server.Add(Single, ClientFixture.CorruptDbc(Codes('R', 10)));
        var client = _fixture.Client(cache: false, configure: options => options.VerifyOnDownload = false);
        var request = new DatasusFileRequest(Single) { DbcOptions = new DbcReadOptions { VerifyChecksum = false } };

        using (var reader = await client.OpenReaderAsync(request, Ct))
        {
            Assert.Equal(10, ReadToTheEnd(reader));
        }

        using (var reader = await client.OpenStreamingReaderAsync(request, Ct))
        {
            Assert.Equal(10, ReadToTheEnd(reader));
        }

        using (var reader = await client.OpenReaderAsync(Request(Single), Ct))
        {
            Assert.Throws<InvalidDataException>(() => ReadToTheEnd(reader));
        }
    }

    [Fact]
    public async Task DownloadAsync_RefusesWhatDoesNotApplyToIt_BeforeAnythingIsSent()
    {
        _fixture.Server.Add(Single, Content);
        var part = new DatasusFileInfo(Single, "RDMG2407.dbc", Size, FakeServer.Time(0), isDirectory: false);
        var table = new DatasusTableInfo(DatasusCatalog.SihSus.RD, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), isPreliminary: false, Single, [part]);
        var client = _fixture.Client();

        var byTable = await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(new DatasusFileRequest(table), Ct));
        var dbf = await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(new DatasusFileRequest(Single) { DbfOptions = new DbfReadOptions() }, Ct));
        var dbc = await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(new DatasusFileRequest(Single) { DbcOptions = new DbcReadOptions() }, Ct));

        Assert.Equal("request", byTable.ParamName);
        Assert.Contains("DatasusTableInfo.Parts", byTable.Message, StringComparison.Ordinal);
        Assert.Contains("DbfOptions and DbcOptions", dbf.Message, StringComparison.Ordinal);
        Assert.Contains("DbfOptions and DbcOptions", dbc.Message, StringComparison.Ordinal);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
    }

    [Fact]
    public async Task BadRequests_FailAsTheOverloadsDo_BeforeAnythingIsSent()
    {
        var client = _fixture.Client();

        Assert.Throws<ArgumentNullException>(() => new DatasusFileRequest((string)null!));
        Assert.Throws<ArgumentNullException>(() => new DatasusFileRequest((DatasusTableInfo)null!));
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => client.DownloadAsync((DatasusFileRequest)null!, Ct))).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenReaderAsync((DatasusFileRequest)null!, Ct))).ParamName);
        Assert.Equal("request", (await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenStreamingReaderAsync((DatasusFileRequest)null!, Ct))).ParamName);

        const string NotAcceptable = "/X/../RDMG2407.dbc";
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadAsync(new DatasusFileRequest(NotAcceptable), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(new DatasusFileRequest(NotAcceptable), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenStreamingReaderAsync(new DatasusFileRequest(NotAcceptable), Ct));
        Assert.Equal(0, client.OpenStreamingReaders);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);

        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.DownloadAsync(new DatasusFileRequest(Single), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenReaderAsync(new DatasusFileRequest(Single), Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.OpenStreamingReaderAsync(new DatasusFileRequest(Single), Ct));
    }

    // With the FTP library in between, on both targets: what ResumedAt says is the REST the server saw.
    [Fact(Timeout = 120_000)]
    public async Task OverTheFtpLibrary_ResumedAtIsTheRestTheServerSaw_InADownloadAndReadingWithoutADownload()
    {
        var ct = TestContext.Current.CancellationToken;
        const long Cut = 5000;
        Assert.True(Size > Cut);
        using var server = new LoopbackFtpServer { AdvertiseEpsv = false };
        server.Files[FakeServer.Root + Single] = Content;
        var options = TestTimes.Loopback(server.Port);
        options.InitialRetryDelay = TimeSpan.Zero;
        options.MaxRetries = 2 + (int)(Size / Cut);
        await using var client = new DatasusClient(options);
        server.EndTransferAfter = Cut;

        await client.DownloadAsync(Request(Single), ct);

        Assert.Equal(Cut, server.Restarts.First());
        Assert.Contains((Downloading, Cut, Size, Cut), _progress.Shape);
        Assert.Contains(_progress.Shape, report => report.Stage == Downloading && report.Done == Size);

        var streaming = new Recorder();
        var part = new DatasusFileInfo(Single, "RDMG2407.dbc", Size, null, isDirectory: false);
        var table = new DatasusTableInfo(DatasusCatalog.SihSus.RD, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), isPreliminary: false, Single, [part]);
        using (var reader = await client.OpenStreamingReaderAsync(new DatasusFileRequest(table) { Progress = streaming }, ct))
        {
            Assert.Equal(9000, ReadToTheEnd(reader));
        }

        Assert.Contains((Streaming, Cut, Size, Cut), streaming.Shape);
        Assert.All(streaming.Shape, report => Assert.Equal(Streaming, report.Stage));
    }

    private static string[] Codes(char letter, int count) =>
        [.. Enumerable.Range(0, count).Select(i => letter + (i % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture))];

    private static byte[] Table() => SyntheticDbc.BuildDbc(new DbfBuilder()
        .Field("CODE", 'C', 4).Field("QTY", 'N', 4)
        .Record("A1", "****")
        .Record("A2", "   7")
        .Build());

    private static int ReadToTheEnd(DbfReader reader)
    {
        var records = 0;
        while (reader.Read())
        {
            records++;
        }

        return records;
    }

    private DatasusFileRequest Request(string path) => new(path) { Progress = _progress };

    private sealed class Recorder : IProgress<DatasusProgressInfo>
    {
        private readonly ConcurrentQueue<DatasusProgressInfo> _reports = new();
        private readonly ConcurrentQueue<bool> _onThreadOfThePool = new();

        public Action<DatasusProgressInfo>? Then { get; set; }

        public IReadOnlyList<DatasusProgressInfo> Reports => [.. _reports];

        public IReadOnlyList<bool> OnThreadOfThePool => [.. _onThreadOfThePool];

        public IReadOnlyList<(DatasusProgressStage Stage, long Done, long Total, long Resumed)> Shape =>
            [.. _reports.Select(report => (report.Stage, report.BytesDone, report.TotalBytes, report.ResumedAt))];

        public void Report(DatasusProgressInfo value)
        {
            _reports.Enqueue(value);
            _onThreadOfThePool.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
            Then?.Invoke(value);
        }
    }
}
