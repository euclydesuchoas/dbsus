using System.Collections.Concurrent;
using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

// The reader from OpenStreamingReaderAsync on the fake server: the same records as a download,
// nothing on disk, one place of the pool from the opening to the end, at most MaxConnections - 1
// such readers, and every failure from Read after the records already returned.
public sealed class StreamingReaderTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private const string Split = Dados + "/PAMG2407.dbc";
    private const string PartA = Dados + "/PAMG2407a.dbc";
    private const string PartB = Dados + "/PAMG2407b.dbc";
    private const string PartC = Dados + "/PAMG2407c.dbc";
    private const string Single = Dados + "/RDMG2407.dbc";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _fixture.Server.ReleaseHeld();
        _fixture.Dispose();
    }

    [Fact]
    public async Task SingleFile_ReadsTheRecordsOfOpenReaderAsync_AndWritesNothingToDisk()
    {
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client();

        List<string> streamed;
        using (var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct))
        {
            streamed = ReadAll(reader);
        }

        Assert.Empty(FilesUnder(_fixture.CacheDirectory));
        Assert.Empty(FilesUnder(_fixture.TempDirectory));
        using var downloaded = await client.OpenReaderAsync(Single, cancellationToken: Ct);
        Assert.Equal(ReadAll(downloaded), streamed);
        Assert.Equal(300, streamed.Count);
    }

    [Fact]
    public async Task PartStream_ServesTheBytesReadAheadThenTheBody_TheBytesOfTheFile()
    {
        var content = ClientFixture.Dbc(Codes('B', 400));
        _fixture.Server.Add(PartB, content);
        using var pool = new ConnectionPool(_fixture.Server.Factory(), 1);
        var released = 0;
        var session = new StreamingSession(await pool.AcquireAsync(Ct), () => Interlocked.Increment(ref released));
        var ahead = content.Take((int)HeaderAhead(content)).ToArray();
        var data = new List<byte>();

        using (var stream = new StreamingPartStream(session, new ReadAheadStreamResumeTests.Retries(0).Policy, RemotePath.Parse(PartB, FakeServer.Root), new RemoteFileInfo(content.Length, FakeServer.Time(0)), ahead, 16L << 20, Ct))
        {
            var buffer = new byte[97];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                data.AddRange(buffer.Take(read));
            }
        }

        Assert.Equal(content, data);
        Assert.Equal(new long[] { 0 }, _fixture.Server.TransferOffsets);
        await ReadAheadStreamTests.WaitUntilAsync(() => Volatile.Read(ref released) == 1, "the session to let go");
        Assert.True(session.IsReleased);
    }

    [Fact]
    public async Task Session_LetsGoOnce_HoweverManyOfItsEndsComeAbout()
    {
        using var pool = new ConnectionPool(_fixture.Server.Factory(), 1);
        var released = 0;
        var session = new StreamingSession(await pool.AcquireAsync(Ct), () => Interlocked.Increment(ref released));
        session.AddPart();

        session.Failed(new IOException("A part failed."));
        session.PartDisposed();
        session.Release();

        Assert.Equal(1, Volatile.Read(ref released));
        using var next = await pool.AcquireAsync(Ct);
        var another = pool.AcquireAsync(Ct);
        Assert.False(another.IsCompleted);
        next.Dispose();
        (await another).Dispose();
    }

    [Fact]
    public async Task PartStream_DropDiesBeforeTheBytesReadAheadAreDropped_ResumesAfterThem_WithEveryByteOnce()
    {
        var content = ClientFixture.Dbc(Codes('B', 400));
        _fixture.Server.Add(PartB, content);
        _fixture.Server.FailNextTransferAfter(30);
        using var pool = new ConnectionPool(_fixture.Server.Factory(), 1);
        var session = new StreamingSession(await pool.AcquireAsync(Ct), () => { });
        var ahead = content.Take((int)HeaderAhead(content)).ToArray();
        var retries = new ReadAheadStreamResumeTests.Retries(1);
        var data = new List<byte>();

        using (var stream = new StreamingPartStream(session, retries.Policy, RemotePath.Parse(PartB, FakeServer.Root), new RemoteFileInfo(content.Length, FakeServer.Time(0)), ahead, 16L << 20, Ct))
        {
            var buffer = new byte[61];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                data.AddRange(buffer.Take(read));
            }
        }

        Assert.Equal(content, data);
        Assert.Equal(new long[] { 0, ahead.Length }, _fixture.Server.TransferOffsets);
        Assert.Equal(1, Assert.Single(retries.Infos).Attempt);
    }

    [Fact]
    public async Task Parts_ReadInOrderOnOnePlace_WithTheHeadersReadAhead_AndOtherOperationsDoNotWait()
    {
        var (expected, _, _, _) = AddParts();
        var client = _fixture.Client();

        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        Assert.True(reader.Read());
        var codes = new List<string> { reader.GetString(0) };
        Assert.Equal(1, _fixture.Server.MaxActiveConnections);
        await WithinAsync(client.GetInfoAsync(PartC, Ct), "GetInfoAsync");
        await WithinAsync(client.ListAsync("/SIASUS", Ct), "ListAsync");
        codes.AddRange(ReadAll(reader));

        Assert.Equal(expected, codes);
        // The headers of b and c read ahead, then the bodies of a, b and c: all from byte 0.
        Assert.Equal(new long[] { 0, 0, 0, 0, 0 }, _fixture.Server.TransferOffsets);
        Assert.Equal(2, _fixture.Server.MaxActiveConnections);
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    [Fact]
    public async Task Table_ReadsExactlyItsParts_WithoutListingAgain()
    {
        var (_, a, _, c) = AddParts();
        var client = _fixture.Client();
        var table = Table(Info(PartA, a), Info(PartC, c));

        using var reader = await client.OpenStreamingReaderAsync(table, cancellationToken: Ct);

        Assert.Equal(["A001", "A002", "C001", "C002"], ReadAll(reader));
        Assert.Equal(0, _fixture.Server.ListCalls);
        Assert.Equal(new long[] { 0, 0, 0 }, _fixture.Server.TransferOffsets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithoutAFailure_NoTransferStartsBeyondByteZero(bool byTable)
    {
        byte[][] contents = [ClientFixture.Dbc(Codes('A', 500)), ClientFixture.Dbc(Codes('B', 500)), ClientFixture.Dbc(Codes('C', 500))];
        _fixture.Server.Add(PartA, contents[0]);
        _fixture.Server.Add(PartB, contents[1]);
        _fixture.Server.Add(PartC, contents[2]);
        string[] expected = [.. Codes('A', 500), .. Codes('B', 500), .. Codes('C', 500)];
        var client = _fixture.Client();

        using var reader = byTable
            ? await client.OpenStreamingReaderAsync(Table(Info(PartA, contents[0]), Info(PartB, contents[1]), Info(PartC, contents[2])), cancellationToken: Ct)
            : await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(5, _fixture.Server.Transfers);
        Assert.All(_fixture.Server.TransferOffsets, offset => Assert.Equal(0, offset));
        Assert.Empty(_fixture.Delays);
    }

    [Fact]
    public async Task BodiesServedInSmallPieces_TheBytesReadAheadAreDropped_AndEveryRecordComesOnce()
    {
        var (expected, _, _, _) = AddParts();
        _fixture.Server.MaxChunk = 7;
        var client = _fixture.Client();

        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(new long[] { 0, 0, 0, 0, 0 }, _fixture.Server.TransferOffsets);
        Assert.Empty(_fixture.Delays);
    }

    [Fact]
    public async Task DropDiesBeforeTheBytesReadAheadAreDropped_IsATransientFailure_AndResumesAfterThem()
    {
        var (expected, _, b, _) = AddParts();
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 4)
            {
                _fixture.Server.FailNextTransferAfter(30);
            }
        };
        var client = _fixture.Client();

        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(new long[] { 0, 0, 0, 0, HeaderAhead(b), 0 }, _fixture.Server.TransferOffsets);
        Assert.Single(_fixture.Delays);
    }

    [Fact(Timeout = 30_000)]
    public async Task NextPart_StartsOnlyOnceTheTransferBeforeItIsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (expected, _, _, _) = AddParts();
        _fixture.Server.TransferCloseDelay = TimeSpan.FromMilliseconds(200);
        var client = _fixture.Client();

        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);

        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    [Fact(Timeout = 30_000)]
    public async Task PartLeftAfterItsLastByte_ItsTransferEndsOnItsOwn_AndTheNextPartFollows()
    {
        var ct = TestContext.Current.CancellationToken;
        var (expected, _, _, _) = AddParts();

        // The reader of the records lets each part go once it has its last record, while the end
        // of the transfer is still to come: that is no failure, and the next part waits for it.
        _fixture.Server.TransferEndDelay = TimeSpan.FromMilliseconds(200);
        var client = _fixture.Client();

        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);

        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(5, _fixture.Server.Transfers);
        Assert.Empty(_fixture.Delays);
        Assert.Equal(0, _fixture.Server.Overlaps);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
    }

    [Fact]
    public async Task PartRepublishedAfterTheOpening_ThrowsRemoteFileChangedWithBothVersions()
    {
        var (_, _, b, _) = AddParts();
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        var republished = ClientFixture.Dbc("B001", "B002");
        _fixture.Server.Add(PartB, republished, FakeServer.Time(5));
        var codes = new List<string>();

        var exception = Assert.Throws<DatasusRemoteFileChangedException>(() => ReadInto(reader, codes));

        Assert.Equal(["A001", "A002"], codes);
        Assert.Equal(PartB, exception.RemotePath);
        Assert.Equal(PartB, exception.Expected.Path);
        Assert.Equal("PAMG2407b.dbc", exception.Expected.Name);
        Assert.Equal(b.Length, exception.Expected.Size);
        Assert.Equal(FakeServer.Time(0), exception.Expected.LastModifiedUtc);
        Assert.Equal(republished.Length, exception.Current.Size);
        Assert.Equal(FakeServer.Time(5), exception.Current.LastModifiedUtc);
        Assert.False(exception.Current.IsDirectory);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the failed reader to let go");
    }

    [Fact]
    public async Task TableFoundBeforeItsFirstPartChanged_TheOpeningThrowsRemoteFileChanged()
    {
        var (_, a, b, _) = AddParts();
        var client = _fixture.Client();
        var table = Table(Info(PartA, a), Info(PartB, b));
        _fixture.Server.Add(PartA, a, FakeServer.Time(4));

        var exception = await Assert.ThrowsAsync<DatasusRemoteFileChangedException>(() => client.OpenStreamingReaderAsync(table, cancellationToken: Ct));

        Assert.Equal(PartA, exception.RemotePath);
        Assert.Equal(FakeServer.Time(0), exception.Expected.LastModifiedUtc);
        Assert.Equal(FakeServer.Time(4), exception.Current.LastModifiedUtc);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the failed opening to let go");
    }

    [Fact]
    public async Task FileRepublishedBeforeAResumption_ThrowsRemoteFileChanged()
    {
        var content = ClientFixture.Dbc(Codes('R', 3000));
        _fixture.Server.Add(Single, content);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 1)
            {
                _fixture.Server.Add(Single, content, FakeServer.Time(9));
                _fixture.Server.FailNextTransferAfter(5000);
            }
        };
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        var codes = new List<string>();

        var exception = Assert.Throws<DatasusRemoteFileChangedException>(() => ReadInto(reader, codes));

        Assert.InRange(codes.Count, 1, 2999);
        Assert.Equal(content.Length, exception.Expected.Size);
        Assert.Equal(content.Length, exception.Current.Size);
        Assert.Equal(FakeServer.Time(0), exception.Expected.LastModifiedUtc);
        Assert.Equal(FakeServer.Time(9), exception.Current.LastModifiedUtc);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task WrongChecksumInPartB_ThrowsInvalidDataAtTheEndOfB_AndReportsPartIndex1()
    {
        _fixture.Server.Add(PartA, ClientFixture.Dbc("A001", "A002"));
        _fixture.Server.Add(PartB, ClientFixture.CorruptDbc("B001"));
        _fixture.Server.Add(PartC, ClientFixture.Dbc("C001"));
        var mismatches = new ConcurrentQueue<DbcChecksumMismatch>();
        var client = _fixture.Client();
        var reader = await client.OpenStreamingReaderAsync(Split, dbcOptions: new DbcReadOptions { ChecksumMismatch = mismatches.Enqueue }, cancellationToken: Ct);
        var codes = new List<string>();

        Assert.Throws<InvalidDataException>(() => ReadInto(reader, codes));

        Assert.Equal(["A001", "A002", "B001"], codes);
        var mismatch = Assert.Single(mismatches);
        Assert.Equal(1, mismatch.PartIndex);
        Assert.Equal("PAMG2407b.dbc", mismatch.PartName);
        Assert.Equal(1, client.OpenStreamingReaders);
        reader.Dispose();
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the disposed reader to let go");
    }

    [Fact]
    public async Task DropInTheMiddleOfPartB_ResumesWhereItStopped_AndReportsTheRetryOnAnotherThread()
    {
        _fixture.Server.Add(PartA, ClientFixture.Dbc("A001", "A002"));
        var b = ClientFixture.Dbc(Codes('B', 2000));
        _fixture.Server.Add(PartB, b);
        _fixture.Server.Add(PartC, ClientFixture.Dbc("C001"));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 4)
            {
                _fixture.Server.FailNextTransferAfter(3000);
            }
        };
        var retries = new ConcurrentQueue<(DatasusRetryInfo Info, int Thread)>();
        var client = _fixture.Client(configure: options => options.OnRetry = info => retries.Enqueue((info, Environment.CurrentManagedThreadId)));
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        var readingThread = Environment.CurrentManagedThreadId;

        var codes = ReadAll(reader);

        string[] expected = ["A001", "A002", .. Codes('B', 2000), "C001"];
        Assert.Equal(expected, codes);
        // Part b's body starts from byte 0 and drops after 3000 bytes, the bytes read ahead among them.
        Assert.Equal(new long[] { 0, 0, 0, 0, 3000, 0 }, _fixture.Server.TransferOffsets);
        Assert.True(HeaderAhead(b) < 3000);
        var (info, thread) = Assert.Single(retries);
        Assert.Equal("Streaming " + PartB, info.Operation);
        Assert.Equal(PartB, info.RemotePath);
        Assert.Equal(1, info.Attempt);
        Assert.NotEqual(readingThread, thread);
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    [Fact]
    public async Task ResumptionInTheMiddleOfPartBTimesOut_StartsOverFromByteZero_AndEveryRecordComesOnce()
    {
        _fixture.Server.Add(PartA, ClientFixture.Dbc("A001", "A002"));
        _fixture.Server.Add(PartB, ClientFixture.Dbc(Codes('B', 2000)));
        _fixture.Server.Add(PartC, ClientFixture.Dbc("C001"));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 4)
            {
                // Part b's body drops after 3000 bytes, and its resumption with REST does not open.
                _fixture.Server.FailNextTransferAfter(3000);
                _fixture.Server.ResumptionsThatTimeOut = 1;
            }
        };
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        string[] expected = ["A001", "A002", .. Codes('B', 2000), "C001"];
        Assert.Equal(expected, ReadAll(reader));
        Assert.Equal(new long[] { 0, 0, 0, 0, 3000, 0, 0 }, _fixture.Server.TransferOffsets);
        Assert.Equal(2, _fixture.Delays.Count);
    }

    // A file republished, with the same size or another, after a resumption failed to open and
    // before the transfer that would start over from byte 0: the version is asked before that
    // transfer too, so the read ends with the change, after records of the first version alone.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepublishedBeforeTheTransferThatStartsOver_ThrowsRemoteFileChanged_AfterRecordsOfTheFirstVersionAlone(bool sameSize)
    {
        var first = ClientFixture.Dbc(Codes('R', 3000));
        var second = ClientFixture.Dbc(Codes('S', sameSize ? 3000 : 3100));
        Assert.Equal(sameSize, first.Length == second.Length);
        _fixture.Server.Add(Single, first);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 1)
            {
                // The body drops after 5000 bytes, and its resumption with REST does not open.
                _fixture.Server.FailNextTransferAfter(5000);
                _fixture.Server.ResumptionsThatTimeOut = 1;
            }
        };
        var client = _fixture.Client(configure: options => options.OnRetry = info =>
        {
            if (info.Attempt == 2)
            {
                _fixture.Server.Add(Single, second, FakeServer.Time(9));
            }
        });
        using var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        var codes = new List<string>();

        var exception = Assert.Throws<DatasusRemoteFileChangedException>(() => ReadInto(reader, codes));

        Assert.InRange(codes.Count, 1, 2999);
        Assert.Equal(Codes('R', 3000).Take(codes.Count), codes);
        Assert.Equal(first.Length, exception.Expected.Size);
        Assert.Equal(second.Length, exception.Current.Size);
        Assert.Equal(FakeServer.Time(9), exception.Current.LastModifiedUtc);
        Assert.Equal(new long[] { 0, 5000 }, _fixture.Server.TransferOffsets);
    }

    [Fact]
    public async Task PartRemovedAfterTheOpening_ThrowsFileNotFound_AndLetsGo()
    {
        AddParts();
        var client = _fixture.Client();
        var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        _fixture.Server.Remove(PartB);
        var codes = new List<string>();

        Assert.Throws<FileNotFoundException>(() => ReadInto(reader, codes));

        Assert.Equal(["A001", "A002"], codes);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the failed reader to let go");
        reader.Dispose();
    }

    [Fact]
    public async Task ServerRefusesPartB_ThrowsRequestRefusedWithoutRetrying()
    {
        AddParts();
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        _fixture.Server.FailNextOperations(1, () => new DatasusRequestRefusedException("550 Permission denied", "550", null));
        var transfers = _fixture.Server.Transfers;
        var codes = new List<string>();

        var exception = Assert.Throws<DatasusRequestRefusedException>(() => ReadInto(reader, codes));

        Assert.Equal(["A001", "A002"], codes);
        Assert.Equal("550", exception.FtpReplyCode);
        Assert.Equal(transfers, _fixture.Server.Transfers);
        Assert.Empty(_fixture.Delays);
    }

    [Fact]
    public async Task PartFailsAfterItsLastRecord_TheNextReadThrowsThatFailure_AndNoOtherPartIsRead()
    {
        var (_, _, b, _) = AddParts();
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 4)
            {
                _fixture.Server.SendExtraBytes(1, 10);
            }
        };
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        var codes = new List<string>();

        // The server sends more than part b's size after its last byte, which the reader of the
        // records never asks for: the failure comes from the read that goes on to part c.
        var exception = Assert.Throws<DatasusRetriesExhaustedException>(() => ReadInto(reader, codes));
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the failed reader to let go");
        var transfers = _fixture.Server.Transfers;
        var again = Assert.Throws<DatasusRetriesExhaustedException>(() => reader.Read());

        Assert.Equal(["A001", "A002", "B001"], codes);
        Assert.Equal(PartB, exception.RemotePath);
        Assert.Contains("sent more than", exception.Message, StringComparison.Ordinal);
        Assert.Same(exception, again);
        Assert.Equal(transfers, _fixture.Server.Transfers);
        Assert.Equal(4, _fixture.Server.Transfers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneConnection_IsRefusedBeforeAnythingIsSent(bool byTable)
    {
        AddParts();
        var client = _fixture.Client(configure: options => options.MaxConnections = 1);

        var opening = byTable ? client.OpenStreamingReaderAsync(Table(Info(PartA, [])), cancellationToken: Ct) : client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.True(opening.IsFaulted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => opening);
        Assert.Equal(
            "Reading without a download needs MaxConnections of 2 or more: the reader holds one connection while another stays free for listings and sizes. Set DatasusClientOptions.MaxConnections to 2 or more, or use OpenReaderAsync.",
            exception.Message);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
        Assert.Equal(0, _fixture.Server.ListCalls);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task TwoConnections_TheSecondReaderFailsAtOnce()
    {
        AddParts();
        var client = _fixture.Client();
        using var first = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        var (connections, transfers, lists, infos) = Counters();

        var second = client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.True(second.IsFaulted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Equal(
            "1 reader from OpenStreamingReaderAsync is open, the most this client allows with MaxConnections = 2: each holds a connection, and one stays free for other operations. Dispose a reader, or set DatasusClientOptions.MaxConnections higher (at most 4).",
            exception.Message);
        Assert.Equal((connections, transfers, lists, infos), Counters());
        Assert.Equal(1, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task ThreeConnections_TwoReadersFit_AndTheThirdFailsAtOnce()
    {
        var (expected, a, b, c) = AddParts();
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        using var first = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        using var second = await client.OpenStreamingReaderAsync(Table(Info(PartA, a), Info(PartB, b), Info(PartC, c)), cancellationToken: Ct);
        var (connections, transfers, lists, infos) = Counters();

        var third = client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.True(third.IsFaulted);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => third);
        Assert.Equal(
            "2 readers from OpenStreamingReaderAsync are open, the most this client allows with MaxConnections = 3: each holds a connection, and one stays free for other operations. Dispose a reader, or set DatasusClientOptions.MaxConnections higher (at most 4).",
            exception.Message);
        Assert.Equal((connections, transfers, lists, infos), Counters());
        Assert.Equal(expected, ReadAll(first));
        Assert.Equal(expected, ReadAll(second));
    }

    [Fact]
    public async Task Disposed_LetsThePlaceGo()
    {
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        Assert.True(reader.Read());

        reader.Dispose();

        await AssertEveryPlaceFreeAsync(client, 3);
    }

    [Fact]
    public async Task ReadToTheEnd_LetsThePlaceGoBeforeItIsDisposed()
    {
        var (expected, _, _, _) = AddParts();
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.Equal(expected, ReadAll(reader));

        await AssertEveryPlaceFreeAsync(client, 3);
        reader.Dispose();
        await AssertEveryPlaceFreeAsync(client, 3);
    }

    [Fact]
    public async Task OpeningFailsBeforeThePlace_LetsTheReservationGo()
    {
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenStreamingReaderAsync(Dados + "/NOPE2407.dbc", cancellationToken: Ct));
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenStreamingReaderAsync(Table(new DatasusFileInfo(PartA, "PAMG2407a.dbc", 10, null, isDirectory: false)), cancellationToken: Ct));

        Assert.Equal(0, client.OpenStreamingReaders);
        await AssertEveryPlaceFreeAsync(client, 3);
    }

    [Fact]
    public async Task OpeningFailsOnThePlace_LetsThePlaceAndTheReservationGo()
    {
        var (_, a, b, _) = AddParts();
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        _fixture.Server.Remove(PartB);

        // The header of part b is read on the place, and is not there.
        await Assert.ThrowsAsync<FileNotFoundException>(() => client.OpenStreamingReaderAsync(Table(Info(PartA, a), Info(PartB, b)), cancellationToken: Ct));
        await AssertEveryPlaceFreeAsync(client, 3);

        // Every header was read, and part a's body had started, when the schemas did not match.
        var other = SyntheticDbc.BuildDbc(new DbfBuilder().Field("OTHER", 'N', 3).Record("1").Build());
        _fixture.Server.Add(PartB, other);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.OpenStreamingReaderAsync(Table(Info(PartA, a), Info(PartB, other)), cancellationToken: Ct));
        await AssertEveryPlaceFreeAsync(client, 3);
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    [Fact]
    public async Task ReadFailsForGood_LetsThePlaceGoBeforeItIsDisposed()
    {
        AddParts();
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 4)
            {
                _fixture.Server.FailNextTransferAfter(10);
            }
        };
        var client = _fixture.Client(configure: options =>
        {
            options.MaxConnections = 3;
            options.MaxRetries = 0;
        });
        var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        Assert.Throws<DatasusRetriesExhaustedException>(() => ReadAll(reader));

        await AssertEveryPlaceFreeAsync(client, 3);
        reader.Dispose();
        await AssertEveryPlaceFreeAsync(client, 3);
    }

    [Fact]
    public async Task LettingGoTwice_LetsOnlyOnePlaceGo()
    {
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        using var kept = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        ReadAll(reader);

        reader.Dispose();
        reader.Dispose();
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 1, "one reader to let go");

        using var other = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.OpenStreamingReaderAsync(Single, cancellationToken: Ct));
        Assert.Equal(2, client.OpenStreamingReaders);
    }

    [Fact(Timeout = 60_000)]
    public async Task LimitFull_WithADownloadAndAListingOnTheOtherPlaces_ReadersGoOnToTheirNextParts()
    {
        var ct = TestContext.Current.CancellationToken;
        var (expected, _, _, _) = AddParts();
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 300)));
        _fixture.Server.Hold = (path, _) => path.EndsWith(Single, StringComparison.Ordinal);
        var client = _fixture.Client(configure: options => options.MaxConnections = 3);
        using var first = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);
        using var second = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);

        var download = Task.Run(() => client.DownloadAsync(Single, ct), ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.Held == 1, "the download to hold the last place");
        var listing = Task.Run(() => client.ListAsync("/SIM", ct), ct);
        var reading = Task.WhenAll(Task.Run(() => ReadAll(first), ct), Task.Run(() => ReadAll(second), ct));

        var codes = await WithinAsync(reading, "Reading both readers");
        Assert.All(codes, read => Assert.Equal(expected, read));
        Assert.False(download.IsCompleted);
        _fixture.Server.ReleaseHeld();
        await WithinAsync(download, "The download");
        await WithinAsync(listing, "The listing");
        Assert.InRange(_fixture.Server.MaxActiveConnections, 2, 3);
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    [Fact(Timeout = 60_000)]
    public async Task DisposedFromAnotherThread_WhileTheNextPartOpens_TheReadEndsWithObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        AddParts();
        // Part b's second transfer is its body; the first read its header ahead.
        var transfersOfB = 0;
        _fixture.Server.Hold = (path, _) => path.EndsWith(PartB, StringComparison.Ordinal) && Interlocked.Increment(ref transfersOfB) == 2;
        var client = _fixture.Client();
        var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);
        var codes = new ConcurrentQueue<string>();
        var reading = Task.Run(
            () =>
            {
                while (reader.Read())
                {
                    codes.Enqueue(reader.GetString(0));
                }
            },
            ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.Held == 1, "the body of part b to be held");
        Assert.False(reading.IsCompleted);

        reader.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => WithinAsync(reading, "The read"));
        Assert.Equal(["A001", "A002"], codes);

        // The pump of part b still has the connection, until the server lets it go.
        Assert.Equal(1, client.OpenStreamingReaders);
        _fixture.Server.ReleaseHeld();
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
    }

    [Fact]
    public async Task CallerTokenCanceled_TheReadsThrowOperationCanceled()
    {
        AddParts();
        var client = _fixture.Client();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: cancellation.Token);
        Assert.True(reader.Read());

        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ReadAll(reader));
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the canceled reader to let go");
    }

    [Fact]
    public async Task ClientDisposed_ThePartUnderWayEnds_AndTheNextThrowsObjectDisposed()
    {
        AddParts();
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        Assert.True(reader.Read());
        var codes = new List<string> { reader.GetString(0) };

        client.Dispose();
        var exception = Assert.Throws<ObjectDisposedException>(() => ReadInto(reader, codes));

        Assert.Equal(["A001", "A002"], codes);
        Assert.Contains(PartB + " could not start: the client was disposed", exception.Message, StringComparison.Ordinal);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.ActiveConnections == 0, "every connection to close");
    }

    [Fact(Timeout = 60_000)]
    public async Task ClientDisposedInAPartLargerThanTheBuffer_ThePartIsReadToItsEnd_AndTheNextThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var records = Codes('A', 300_000);
        var a = ClientFixture.Dbc(records);
        var b = ClientFixture.Dbc("B001");
        Assert.True(a.Length > RemoteReadAheadStream.Capacity, a.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _fixture.Server.Add(PartA, a);
        _fixture.Server.Add(PartB, b);
        var client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: ct);
        var codes = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            Assert.True(reader.Read());
            codes.Add(reader.GetString(0));
        }

        // The buffer holds at most 1 MiB ahead of the reader: part a's transfer is still under way.
        Assert.True(_fixture.Server.BytesServed < a.Length, _fixture.Server.BytesServed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        client.Dispose();
        var exception = Assert.Throws<ObjectDisposedException>(() => ReadInto(reader, codes));

        Assert.Equal(records, codes);
        Assert.Contains(PartB + " could not start: the client was disposed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(HeaderAhead(b) + a.Length, _fixture.Server.BytesServed);
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the reader to let go");
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.ActiveConnections == 0, "every connection to close");
    }

    [Fact]
    public async Task ClientDisposed_AResumptionThrowsObjectDisposed()
    {
        _fixture.Server.Add(Single, ClientFixture.Dbc(Codes('R', 3000)));
        DatasusClient? client = null;
        _fixture.Server.BeforeTransfer = transfer =>
        {
            client!.Dispose();
            _fixture.Server.FailNextTransferAfter(5000);
        };
        client = _fixture.Client();
        using var reader = await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
        var codes = new List<string>();

        var exception = Assert.Throws<ObjectDisposedException>(() => ReadInto(reader, codes));

        Assert.InRange(codes.Count, 1, 2999);
        Assert.Contains("could not resume", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task Disposed_ReadThrowsObjectDisposed()
    {
        AddParts();
        var client = _fixture.Client();
        var reader = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);

        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.Read());
    }

    [Fact(Timeout = 120_000)]
    public async Task Stress_TwoReadersOverPartsWithDrops_ReadEveryRecordAndLetGo()
    {
        var ct = TestContext.Current.CancellationToken;
        string[][] parts = [Codes('A', 300), Codes('B', 300), Codes('C', 300)];
        _fixture.Server.Add(PartA, ClientFixture.Dbc(parts[0]));
        _fixture.Server.Add(PartB, ClientFixture.Dbc(parts[1]));
        _fixture.Server.Add(PartC, ClientFixture.Dbc(parts[2]));
        string[] expected = [.. parts[0], .. parts[1], .. parts[2]];
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer % 4 == 0)
            {
                _fixture.Server.FailNextTransferAfter(100 + (transfer * 37 % 1400));
            }
        };
        var client = _fixture.Client(configure: options =>
        {
            options.MaxConnections = 3;
            options.MaxRetries = 10;
        });

        for (var round = 0; round < 20; round++)
        {
            var readers = await Task.WhenAll(
                Task.Run(() => client.OpenStreamingReaderAsync(Split, cancellationToken: ct), ct),
                Task.Run(() => client.OpenStreamingReaderAsync(Split, cancellationToken: ct), ct));
            var info = client.GetInfoAsync(PartC, ct);
            var codes = await WithinAsync(Task.WhenAll(readers.Select(reader => Task.Run(() => ReadAll(reader), ct))), $"Round {round}");
            await WithinAsync(info, $"The info of round {round}");
            foreach (var reader in readers)
            {
                reader.Dispose();
            }

            Assert.All(codes, read => Assert.Equal(expected, read));
            await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, $"the readers of round {round} to let go");
        }

        Assert.InRange(_fixture.Server.MaxActiveConnections, 1, 3);
        Assert.Equal(0, _fixture.Server.Overlaps);
    }

    // Four-character codes: the letter and three digits.
    private static string[] Codes(char letter, int count) =>
        [.. Enumerable.Range(1, count).Select(i => letter + (i % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture))];

    // What the reader reads ahead of a part: the DBF header and the checksum after it.
    private static long HeaderAhead(byte[] dbc) => (dbc[8] | (dbc[9] << 8)) + 4;

    private static List<string> ReadAll(DbfReader reader)
    {
        var codes = new List<string>();
        ReadInto(reader, codes);
        return codes;
    }

    private static void ReadInto(DbfReader reader, List<string> codes)
    {
        while (reader.Read())
        {
            codes.Add(reader.GetString(0));
        }
    }

    private static IEnumerable<string> FilesUnder(string directory) =>
        Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories) : [];

    private static DatasusFileInfo Info(string path, byte[] content) =>
        new(path, path.Substring(path.LastIndexOf('/') + 1), content.Length, FakeServer.Time(0), isDirectory: false);

    private static DatasusTableInfo Table(params DatasusFileInfo[] parts) =>
        new(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7), isPreliminary: false, Split, parts);

    private static async Task WithinAsync(Task task, string what)
    {
        using var timeout = new CancellationTokenSource();
        var first = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token));
        timeout.Cancel();
        Assert.True(first == task, $"{what} did not end within 10 seconds.");
        await task;
    }

    private static async Task<T> WithinAsync<T>(Task<T> task, string what)
    {
        await WithinAsync((Task)task, what);
        return await task;
    }

    // Every place free: as many readers as the limit open, and one more fails at once.
    private static async Task AssertEveryPlaceFreeAsync(DatasusClient client, int maxConnections)
    {
        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "every streaming reader to let go");
        var readers = new List<DbfReader>();
        try
        {
            for (var i = 0; i < maxConnections - 1; i++)
            {
                readers.Add(await client.OpenStreamingReaderAsync(Single, cancellationToken: Ct));
            }

            var extra = client.OpenStreamingReaderAsync(Single, cancellationToken: Ct);
            Assert.True(extra.IsFaulted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => extra);
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }

        await ReadAheadStreamTests.WaitUntilAsync(() => client.OpenStreamingReaders == 0, "the readers of the check to let go");
    }

    private (string[] Expected, byte[] A, byte[] B, byte[] C) AddParts()
    {
        var a = ClientFixture.Dbc("A001", "A002");
        var b = ClientFixture.Dbc("B001");
        var c = ClientFixture.Dbc("C001", "C002");
        _fixture.Server.Add(PartA, a);
        _fixture.Server.Add(PartB, b);
        _fixture.Server.Add(PartC, c);
        return (["A001", "A002", "B001", "C001", "C002"], a, b, c);
    }

    private (int Connections, int Transfers, int Lists, int Infos) Counters() =>
        (_fixture.Server.ConnectionsOpened, _fixture.Server.Transfers, _fixture.Server.ListCalls, _fixture.Server.InfoCalls);
}
