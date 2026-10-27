using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;
using static DbSus.Client.Tests.ReadAheadStreamResumeTests;
using static DbSus.Client.Tests.ReadAheadStreamTests;

namespace DbSus.Client.Tests;

// A partial download, from an earlier attempt or from another client, resumes with REST, whose
// opening has its own limit. Only after a resumption of the same call failed to open is it
// downloaded again from byte 0, without REST: when it holds at most the restart size, or on the
// last attempt of the budget; otherwise REST again. A failure in the middle of a transfer that
// opened is no such evidence. The DBC's CRC is checked as always. The REST the client sends is
// FluentFTP's: in upper case, in one piece.
public sealed class DownloadRestartTests : IDisposable
{
    private const string FilePath = "/X/RDMG2407.dbc";
    private const string FullPath = FakeServer.Root + FilePath;
    private readonly ClientFixture _fixture = new();
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly byte[] _content = ClientFixture.Dbc([.. Enumerable.Range(0, 9000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);

    public DownloadRestartTests() => Assert.True(_content.Length > 20_000, _content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose()
    {
        _server.Dispose();
        _fixture.Dispose();
    }

    [Fact]
    public async Task DropsEveryFewBytes_BelowTheRestartSize_CompleteWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, _content);
        _fixture.Server.FailNextTransferAfter(4000);
        _fixture.Server.FailNextTransferAfter(4000);

        var path = await _fixture.Client().DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 4000L, 8000L], _fixture.Server.TransferOffsets);
        Assert.Equal(_content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task FiveConnections_FourRetries_CompleteOnTheFifthWithRest_WithoutDeletingThePartialFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var step = (_content.Length / 5) + 1;
        _fixture.Server.Add(FilePath, _content);
        for (var i = 0; i < 4; i++)
        {
            _fixture.Server.FailNextTransferAfter(step);
        }

        var infos = new List<DatasusRetryInfo>();

        // The fifth attempt is the last of the budget, and it still resumes: no resumption failed to open.
        var path = await _fixture.Client(configure: options =>
        {
            options.MaxRetries = 4;
            options.OnRetry = infos.Add;
        }).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => (long)i * step), _fixture.Server.TransferOffsets);
        Assert.Equal(_content.Length, _fixture.Server.BytesServed);
        Assert.Equal([1, 2, 3, 4], infos.Select(info => info.Attempt));
    }

    [Fact]
    public async Task FailuresInTheMiddleOfTransfersThatOpened_AreNoEvidence_EvenOnTheLastAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, _content);
        for (var i = 0; i < 3; i++)
        {
            _fixture.Server.FailNextTransferAfter(3000);
        }

        var path = await _fixture.Client(configure: options => options.MaxRetries = 3).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 3000L, 6000L, 9000L], _fixture.Server.TransferOffsets);
        Assert.Equal(_content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task RestUnansweredOnce_SmallPartial_TheNextAttemptDownloadsTheFileWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, _content);
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.ResumptionsThatTimeOut = 1;
        var infos = new List<DatasusRetryInfo>();

        var path = await _fixture.Client(configure: options => options.OnRetry = infos.Add).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal(5000 + _content.Length, _fixture.Server.BytesServed);
        Assert.Equal([1, 2], infos.Select(info => info.Attempt));
        Assert.True(Assert.IsType<TransientTransportException>(infos[1].Exception).IsExpiredLimit);
    }

    // The version is asked before every attempt that goes on with a partial download, the one that
    // starts over from byte 0 included, as it was before there was any: a file republished after the
    // resumption failed to open is found before the next transfer, and downloaded once, whole, in
    // its new version.
    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-100)]
    public async Task RepublishedBeforeTheAttemptThatStartsOver_IsFoundBeforeItsTransfer(int sizeChange)
    {
        var ct = TestContext.Current.CancellationToken;
        const string path = "/X/RDMG2407.dbf";
        byte[] first = [.. Enumerable.Range(0, 20_000).Select(i => (byte)(i % 251))];
        byte[] second = [.. Enumerable.Range(0, 20_000 + sizeChange).Select(i => (byte)(i % 241))];
        _fixture.Server.Add(path, first, FakeServer.Time(1));
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.ResumptionsThatTimeOut = 1;

        var local = await _fixture.Client(configure: options => options.OnRetry = info =>
        {
            if (info.Attempt == 2)
            {
                _fixture.Server.Add(path, second, FakeServer.Time(2));
            }
        }).DownloadAsync(path, ct);

        Assert.Equal(second, File.ReadAllBytes(local));
        Assert.Equal([0L, 5000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal(5000 + second.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task RestAlwaysUnanswered_LargePartial_TriesRestAgain_ThenTheLastAttemptDownloadsTheFileWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        _fixture.Server.Add(FilePath, _content);
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.ResumptionsThatTimeOut = int.MaxValue;
        var infos = new List<DatasusRetryInfo>();

        var path = await _fixture.Client(configure: options =>
        {
            options.RestartFromStartUpTo = 1000;
            options.MaxRetries = 3;
            options.OnRetry = infos.Add;
        }).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L, 5000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Equal([1, 2, 3], infos.Select(info => info.Attempt));
        Assert.All(infos.Skip(1), info => Assert.True(Assert.IsType<TransientTransportException>(info.Exception).IsExpiredLimit));
    }

    [Fact]
    public async Task OneRetry_LargePartial_TheResumptionFailsToOpen_ThenTheLastAttemptDownloadsTheFileWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        await LeavePartialAsync(ct);
        _fixture.Server.ResumptionsThatTimeOut = int.MaxValue;

        var path = await _fixture.Client(configure: options =>
        {
            options.MaxRetries = 1;
            options.RestartFromStartUpTo = 4999;
        }).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L, 0L], _fixture.Server.TransferOffsets);
        Assert.Empty(PartialFiles());
    }

    [Fact]
    public async Task SmallPartialFromAnotherClient_IsResumedWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        await LeavePartialAsync(ct);

        var path = await _fixture.Client().DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L], _fixture.Server.TransferOffsets);
        Assert.Equal(_content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task NoRetries_SmallPartial_WithoutEvidence_IsResumedWithRest()
    {
        var ct = TestContext.Current.CancellationToken;
        await LeavePartialAsync(ct);

        var path = await _fixture.Client(configure: options => options.MaxRetries = 0).DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L], _fixture.Server.TransferOffsets);
        Assert.Equal(_content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task NoRetries_LargePartial_RestUnanswered_TheFailureIsThrown_AndThePartialKept()
    {
        var ct = TestContext.Current.CancellationToken;
        await LeavePartialAsync(ct);
        _fixture.Server.ResumptionsThatTimeOut = int.MaxValue;
        var infos = new List<DatasusRetryInfo>();

        var failure = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => _fixture.Client(configure: options =>
        {
            options.MaxRetries = 0;
            options.RestartFromStartUpTo = 4999;
            options.OnRetry = infos.Add;
        }).DownloadAsync(FilePath, ct));

        Assert.Equal(1, failure.Attempts);
        Assert.True(Assert.IsType<TransientTransportException>(failure.InnerException).IsExpiredLimit);
        Assert.Empty(infos);
        Assert.Equal([0L, 5000L], _fixture.Server.TransferOffsets);
        Assert.Equal(5000, new FileInfo(Assert.Single(PartialFiles())).Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_RestUnansweredOnce_TheNextAttemptSendsRetrWithoutRest_AndTheCrcHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files[FullPath] = _content;
        _server.StallTransferAfter = 3000;
        var infos = new List<DatasusRetryInfo>();
        using var client = Loopback(options =>
        {
            options.MaxRetries = 3;
            options.ResumeOpenTimeout = TimeSpan.FromMilliseconds(300);
            options.OnRetry = info =>
            {
                // After the stall, the REST goes unanswered once; after that, the server answers again.
                infos.Add(info);
                _server.StallTransferAfter = null;
                _server.SilentOn = infos.Count == 1 ? "REST" : null;
            };
        });

        var path = await client.DownloadAsync(FilePath, ct);

        // The client checks the CRC-32 of a .dbc it downloads (VerifyOnDownload); the bytes are the file's.
        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(1, _server.Commands.Count(command => command == "REST"));
        Assert.Equal(2, _server.Commands.Count(command => command == "RETR"));
        Assert.Equal([1, 2], infos.Select(info => info.Attempt));
        Assert.True(Assert.IsType<TransientTransportException>(infos[1].Exception).IsExpiredLimit);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_LargePartial_RestUnanswered_TheLastAttemptDownloadsTheFileWhole()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files[FullPath] = _content;
        _server.StallTransferAfter = 3000;
        var infos = new List<DatasusRetryInfo>();
        using var client = Loopback(options =>
        {
            options.MaxRetries = 2;
            options.RestartFromStartUpTo = 1000;
            options.ResumeOpenTimeout = TimeSpan.FromMilliseconds(300);
            options.OnRetry = info =>
            {
                infos.Add(info);
                _server.StallTransferAfter = null;
                _server.SilentOn = "REST";
            };
        });

        var path = await client.DownloadAsync(FilePath, ct);

        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(1, _server.Commands.Count(command => command == "REST"));
        Assert.Equal(2, _server.Commands.Count(command => command == "RETR"));
        Assert.Equal([1, 2], infos.Select(info => info.Attempt));
        Assert.True(Assert.IsType<TransientTransportException>(infos[1].Exception).IsExpiredLimit);
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_CallerCanceledWhileTheResumptionOpens_IsACancellation_NotAnAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files[FullPath] = _content;
        _server.StallTransferAfter = 3000;
        var infos = new List<DatasusRetryInfo>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var client = Loopback(options =>
        {
            options.MaxRetries = 3;
            options.ResumeOpenTimeout = TimeSpan.FromSeconds(20);
            options.OnRetry = info =>
            {
                infos.Add(info);
                _server.StallTransferAfter = null;
                _server.SilentOn = "REST";
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(300));
            };
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(FilePath, cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
        Assert.Single(infos);
        Assert.Equal(1, _server.Commands.Count(command => command == "REST"));
    }

    [Fact(Timeout = 60_000)]
    public async Task Loopback_EveryRestTheClientSends_IsInUpperCase_AndInOnePiece()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files[FullPath] = _content;

        // A download that resumes with REST.
        _server.StallTransferAfter = 3000;
        using (var client = Loopback(options =>
        {
            options.MaxRetries = 2;
            options.OnRetry = _ => _server.StallTransferAfter = null;
        }))
        {
            Assert.Equal(_content, File.ReadAllBytes(await client.DownloadAsync(FilePath, ct)));
        }

        // A read-ahead stream that resumes with REST.
        _server.StallTransferAfter = 4000;
        var retries = new Retries(2, (_, _) =>
        {
            _server.StallTransferAfter = null;
            return Task.CompletedTask;
        });
        using (var pool = LoopbackPool(_server, TimeSpan.FromMilliseconds(500)))
        using (var stream = await RemoteReadAheadStream.OpenAsync(pool, retries.Policy, RemotePath.Parse(FilePath, FakeServer.Root), Version(_content.Length), ct))
        {
            var (data, failure) = ReadAll(stream, new Random(301));
            Assert.Null(failure);
            AssertSameBytes(_content, data);
        }

        var rests = _server.CommandLines.Where(line => line.Line.StartsWith("REST", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, rests.Count);
        Assert.All(rests, rest =>
        {
            Assert.StartsWith("REST ", rest.Line, StringComparison.Ordinal);
            Assert.True(rest.Line.Length > 5 && rest.Line.Substring(5).All(c => c is >= '0' and <= '9'), rest.Line);
            Assert.Equal(1, rest.Reads);
        });
    }

    // A client without retries whose transfer drops after 5000 bytes: it leaves a 5000-byte partial
    // file in the cache, as another process would, for the next client to find.
    private async Task LeavePartialAsync(CancellationToken cancellationToken)
    {
        _fixture.Server.Add(FilePath, _content);
        _fixture.Server.FailNextTransferAfter(5000);
        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => _fixture.Client(configure: options => options.MaxRetries = 0).DownloadAsync(FilePath, cancellationToken));
        Assert.Equal(5000, new FileInfo(Assert.Single(PartialFiles())).Length);
    }

    private IEnumerable<string> PartialFiles() =>
        Directory.Exists(_fixture.CacheDirectory)
            ? Directory.EnumerateFiles(_fixture.CacheDirectory, "*.partial", SearchOption.AllDirectories)
            : [];

    private DatasusClient Loopback(Action<DatasusClientOptions> configure)
    {
        var options = new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ReadTimeout = TimeSpan.FromMilliseconds(500),
            InitialRetryDelay = TimeSpan.Zero,
            CacheDirectory = Path.Combine(_fixture.Workspace, "loopback-cache"),
        };
        configure(options);
        return new DatasusClient(options);
    }
}
