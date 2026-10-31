using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

public sealed class DatasusClientTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListAsync_ReturnsFilesAndDirectoriesAndReusesTheListing()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/Sub/x.dbc", ClientFixture.Dbc("X"));
        var client = _fixture.Client();

        var first = await client.ListAsync(Dados, Ct);
        var second = await client.ListAsync(Dados + "/", Ct);

        Assert.Equal(["/SIASUS/200801_/Dados/PAMG2407a.dbc", "/SIASUS/200801_/Dados/Sub"], first.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Contains(first, entry => entry.Name == "Sub" && entry.IsDirectory);
        Assert.All(first, entry => Assert.Null(entry.LastModifiedUtc));
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(1, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListAsync_AfterTheListingExpires_FetchesAgain()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();

        await client.ListAsync(Dados, Ct);
        _fixture.Now += TimeSpan.FromMinutes(11);
        await client.ListAsync(Dados, Ct);

        Assert.Equal(2, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task GetInfoAsync_ReturnsExactSizeAndTime()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content, FakeServer.Time(7));

        var info = await _fixture.Client().GetInfoAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(content.Length, info.Size);
        Assert.Equal(FakeServer.Time(7), info.LastModifiedUtc);
        Assert.Equal("/SIASUS/200801_/Dados/RDMG2407.dbc", info.Path);
    }

    [Fact]
    public async Task GetInfoAsync_MissingFile_ThrowsFileNotFoundWithoutRetrying()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _fixture.Client().GetInfoAsync(Dados + "/NOPE2407.dbc", Ct));

        Assert.Equal(1, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task FindPartsAsync_UsesTheListingAndReturnsExactInfoPerPart()
    {
        foreach (var letter in "bac")
        {
            _fixture.Server.Add($"{Dados}/PAMG2407{letter}.dbc", ClientFixture.Dbc("A1"), FakeServer.Time(letter - 'a'));
        }

        _fixture.Server.Add(Dados + "/PAMG2408a.dbc", ClientFixture.Dbc("A1"));

        var parts = await _fixture.Client().FindPartsAsync(Dados + "/PAMG2407.dbc", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc", "PAMG2407c.dbc"], parts.Select(part => part.Name));
        Assert.Equal(FakeServer.Time(1), parts[1].LastModifiedUtc);
    }

    [Fact]
    public async Task FindPartsAsync_GapOnTheServer_ThrowsFileNotFoundNamingThePart()
    {
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/PAMG2407c.dbc", ClientFixture.Dbc("A1"));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _fixture.Client().FindPartsAsync(Dados + "/PAMG2407a.dbc", Ct));

        Assert.Equal("/SIASUS/200801_/Dados/PAMG2407b.dbc", exception.FileName);
    }

    [Fact]
    public async Task FindPartsAsync_UnsplitAndLetteredTogether_ThrowsIOException()
    {
        _fixture.Server.Add(Dados + "/PAMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Add(Dados + "/PAMG2407a.dbc", ClientFixture.Dbc("A1"));

        await Assert.ThrowsAsync<IOException>(() => _fixture.Client().FindPartsAsync(Dados + "/PAMG2407a.dbc", Ct));
    }

    [Fact]
    public async Task ReadHeaderAsync_ReadsOnlyTheHeaderBytes()
    {
        var content = ClientFixture.Dbc("A1", "B2", "C3");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);

        var header = await _fixture.Client().ReadHeaderAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(3, header.RecordCount);
        Assert.Equal(["CODE"], header.Fields.Select(field => field.Name));
        Assert.Equal(header.HeaderLength, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task DownloadAsync_WithoutCache_WritesATemporaryFile()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);

        var path = await _fixture.Client(cache: false).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.StartsWith(_fixture.TempDirectory, path, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    // An operation that waits for a connection when the client is disposed fails at once, and
    // connects nothing; the download under way goes on, and its place is free again at its end.
    [Fact]
    public async Task Dispose_AnOperationWaitingForAConnection_ThrowsObjectDisposed_AndConnectsNothing()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.Hold = (_, _) => true;
        var client = _fixture.Client(cache: false, configure: options => options.MaxConnections = 1);
        var download = Task.Run(() => client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct), Ct);
        await ReadAheadStreamTests.WaitUntilAsync(() => _fixture.Server.Held == 1, "the download to hold the only connection");
        var info = client.GetInfoAsync(Dados + "/RDMG2407.dbc", Ct);
        Assert.False(info.IsCompleted);

        client.Dispose();

        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => info);
        Assert.Equal(typeof(DatasusClient).FullName, exception.ObjectName);
        _fixture.Server.ReleaseHeld();
        await download;
        Assert.Equal(1, _fixture.Server.ConnectionsOpened);
        Assert.Equal(1, client.FreeConnectionPlaces);
    }

    // The first download without a cache starts the clean-up of the temporary folders on the thread
    // pool; a later download of the same client does not start another one.
    [Fact]
    public async Task DownloadAsync_WithoutCache_FirstDownloadCleansUpOldFolders_OnlyOnce()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        var orphan = Path.Combine(_fixture.TempDirectory, "DbSus-0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "entry.lock"), string.Empty);
        File.WriteAllText(Path.Combine(orphan, "RDMG2407.dbc.partial"), "synthetic");
        _fixture.Now = DateTime.UtcNow.AddDays(2);
        var client = _fixture.Client(cache: false);

        await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);
        var first = client.TempCleanup;
        await first;
        await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.False(Directory.Exists(orphan));
        Assert.Same(first, client.TempCleanup);
    }

    // The clean-up that the first download without a cache starts runs next to it, on the thread
    // pool, and may look at the temporary directory at any point of that download. Here it is held
    // (its first step reads the clock) until the download is over, and then runs with a clock two
    // days ahead, so that every folder looks old: the folder of the download that started it is
    // still left alone, and so is the file the download returned.
    [Fact]
    public async Task DownloadAsync_WithoutCache_TheCleanUpItStarts_LeavesItsOwnFolder()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        using var release = new ManualResetEventSlim();
        var later = DateTime.UtcNow.AddDays(2);
        var held = false;
        var client = _fixture.Client(cache: false, clock: () =>
        {
            if (new System.Diagnostics.StackTrace().GetFrames().Any(frame => frame.GetMethod()?.Name == nameof(Caching.TempStore.CleanOrphans)))
            {
                held = true;
                release.Wait(TimeSpan.FromSeconds(30));
                return later;
            }

            return _fixture.Now;
        });

        var path = await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);
        release.Set();
        await client.TempCleanup;

        // Read only once the clean-up is over; without it the test would prove nothing.
        Assert.True(held, "The clock was never asked from inside TempStore.CleanOrphans, so the clean-up was not held.");
        Assert.True(File.Exists(path));
    }

    // Whatever a clean-up throws (but a failure of the process itself), its task ends without fault,
    // so no exception goes unobserved.
    [Fact]
    public async Task StartCleanup_ActionThrows_TaskEndsWithoutFault()
    {
        var task = DatasusClient.StartCleanup(() => throw new InvalidOperationException("synthetic"));

        await task;

        Assert.Equal(TaskStatus.RanToCompletion, task.Status);
    }

    [Fact]
    public async Task DownloadAsync_WithCache_ReusesTheEntryAndRecordsTheUse()
    {
        var content = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content, FakeServer.Time(3));
        var client = _fixture.Client();

        var first = await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);
        _fixture.Now += TimeSpan.FromHours(1);
        var second = await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(first, second);
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.Equal(content, File.ReadAllBytes(second));
        var meta = File.ReadAllText(Path.Combine(Path.GetDirectoryName(second)!, "entry.meta"));
        Assert.Contains("path=/SIASUS/200801_/Dados/RDMG2407.dbc\n", meta, StringComparison.Ordinal);
        Assert.Contains("lastUsedUtc=2026-09-30T13:00:00.0000000Z\n", meta, StringComparison.Ordinal);
        Assert.Contains("crcVerified=true\n", meta, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_ConnectionDropsMidTransfer_ResumesWhereItStopped()
    {
        var content = ClientFixture.Dbc([.. Enumerable.Range(0, 2000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);
        _fixture.Server.FailNextTransferAfter(5000);

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(content, File.ReadAllBytes(path));
        Assert.Equal([0L, 5000L], _fixture.Server.TransferOffsets);
        Assert.Equal(content.Length, _fixture.Server.BytesServed);
    }

    [Fact]
    public async Task DownloadAsync_FileRepublishedBeforeResuming_RestartsFromZeroAsANewVersion()
    {
        var original = ClientFixture.Dbc([.. Enumerable.Range(0, 2000).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]);
        var republished = ClientFixture.Dbc("NEW1", "NEW2");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", original, FakeServer.Time(1));
        _fixture.Server.FailNextTransferAfter(5000);
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(Dados + "/RDMG2407.dbc", republished, FakeServer.Time(2));
            }
        };

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(republished, File.ReadAllBytes(path));
        Assert.EndsWith("-20250808120200", Path.GetDirectoryName(path), StringComparison.Ordinal);
        Assert.Equal(0L, _fixture.Server.TransferOffsets.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadAsync_FileRepublishedAtEveryAttempt_ThrowsRetriesExhaustedSayingItChanged(bool openReader)
    {
        var content = ClientFixture.Dbc("A1", "B2");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", content);
        _fixture.Server.BeforeTransfer = transfer => _fixture.Server.Add(Dados + "/RDMG2407.dbc", content, FakeServer.Time(transfer));
        var client = _fixture.Client();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(async () =>
        {
            if (openReader)
            {
                using var reader = await client.OpenReaderAsync(Dados + "/RDMG2407.dbc", cancellationToken: Ct);
            }
            else
            {
                await client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct);
            }
        });

        Assert.Contains("changed on the server at every attempt", exception.Message, StringComparison.Ordinal);
        Assert.Equal("/SIASUS/200801_/Dados/RDMG2407.dbc", exception.RemotePath);
        Assert.Equal(4, exception.Attempts);
        Assert.Null(exception.FtpReplyCode);
        Assert.Equal(4, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_TransientConnectFailures_AreRetried()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(2, () => new TransientTransportException("421 Too many connections"));

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task DownloadAsync_RetriesExhausted_ThrowsRetriesExhaustedWithDetails()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(10, () => new TransientTransportException("421 Too many connections", "421"));

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => _fixture.Client(configure: o => o.MaxRetries = 2).DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("failed after 3 attempts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, exception.Attempts);
        Assert.Equal("421", exception.FtpReplyCode);
        Assert.Equal(Dados + "/RDMG2407.dbc", exception.RemotePath);
    }

    [Fact]
    public async Task DownloadAsync_ServerRefuses_ThrowsRequestRefusedWithoutRetrying()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(10, () => new DatasusRequestRefusedException("530 Not logged in", "530", null));

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Equal(1, exception.Attempts);
        Assert.Equal("530", exception.FtpReplyCode);
        Assert.Equal(Dados + "/RDMG2407.dbc", exception.RemotePath);
    }

    [Fact]
    public async Task OnRetry_ReceivesEveryRetryOfTheClient()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.Server.FailNextConnects(2, () => new TransientTransportException("421 Too many connections", "421"));
        var reports = new List<DatasusRetryInfo>();

        await _fixture.Client(configure: o => o.OnRetry = reports.Add).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal([1, 2], reports.Select(report => report.Attempt));
        Assert.All(reports, report => Assert.Contains(Dados + "/RDMG2407.dbc", report.Operation, StringComparison.Ordinal));
        Assert.All(reports, report => Assert.Equal(Dados + "/RDMG2407.dbc", report.RemotePath));
    }

    [Fact]
    public async Task DownloadAsync_CorruptOnBothDownloads_ThrowsAndCachesNothing()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("failed its CRC-32 check after two downloads", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, _fixture.Server.Transfers);
        Assert.Empty(Directory.EnumerateFiles(_fixture.CacheDirectory, "RDMG2407.dbc*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_CorruptOnceThenGood_SucceedsOnTheSecondDownload()
    {
        var good = ClientFixture.Dbc("A1");
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2)
            {
                _fixture.Server.Add(Dados + "/RDMG2407.dbc", good);
            }
        };

        var path = await _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(good, File.ReadAllBytes(path));
        Assert.Equal(2, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_VerificationDisabled_KeepsTheFileUnchecked()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc("A1"));

        var path = await _fixture.Client(configure: o => o.VerifyOnDownload = false).DownloadAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.True(File.Exists(path));
        Assert.Contains("crcVerified=false", File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "entry.meta")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReaderAsync_VerificationDisabled_ReadsAfterTheChecksumFailureThrowItAgain()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.CorruptDbc([.. Enumerable.Range(1, 300).Select(i => "R" + (i % 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture))]));
        var client = _fixture.Client(configure: o => o.VerifyOnDownload = false);
        using var reader = await client.OpenReaderAsync(Dados + "/RDMG2407.dbc", cancellationToken: Ct);
        var first = Assert.Throws<InvalidDataException>(() =>
        {
            while (reader.Read())
            {
            }
        });

        var again = Assert.Throws<InvalidDataException>(() => reader.Read());

        Assert.Equal(first.Message, again.Message);
    }

    [Fact]
    public async Task DownloadAsync_NotEnoughDiskSpace_ThrowsBeforeTransferring()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        _fixture.FreeSpace = 1024;

        var exception = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", Ct));

        Assert.Contains("Not enough disk space", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1024, exception.AvailableBytes);
        Assert.True(exception.RequiredBytes > exception.AvailableBytes);
        Assert.False(string.IsNullOrEmpty(exception.Directory));
        Assert.Equal(0, _fixture.Server.Transfers);
    }

    [Fact]
    public async Task DownloadAsync_Canceled_ThrowsAndLeavesNoCachedFile()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));
        using var cancellation = new CancellationTokenSource();
        _fixture.Server.BeforeTransfer = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fixture.Client().DownloadAsync(Dados + "/RDMG2407.dbc", cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(_fixture.CacheDirectory, "RDMG2407.dbc", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadAsync_ConcurrentRequestsForOneFile_TransferItOnce()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc([.. Enumerable.Range(0, 500).Select(i => i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))]));
        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        var client = _fixture.Client();

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => client.DownloadAsync(Dados + "/RDMG2407.dbc", Ct))));

        Assert.Single(paths.Distinct());
        Assert.Equal(1, _fixture.Server.Transfers);
        Assert.InRange(_fixture.Server.MaxActiveConnections, 1, 2);
    }

    [Fact]
    public async Task DownloadAsync_ManyFilesInParallel_NeverExceedsMaxConnections()
    {
        for (var i = 0; i < 6; i++)
        {
            _fixture.Server.Add($"{Dados}/RD{i:D2}2407.dbc", ClientFixture.Dbc("A1", "B2"));
        }

        _fixture.Server.ChunkDelay = TimeSpan.FromMilliseconds(10);
        var client = _fixture.Client(configure: o => o.MaxConnections = 2);

        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => client.DownloadAsync($"{Dados}/RD{i:D2}2407.dbc", Ct))));

        Assert.Equal(6, _fixture.Server.Transfers);
        Assert.InRange(_fixture.Server.MaxActiveConnections, 1, 2);
    }

    [Fact]
    public async Task Methods_AfterDispose_ThrowObjectDisposedException()
    {
        var client = _fixture.Client();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListAsync(Dados, Ct));
    }

    [Fact]
    public async Task Dispose_WhileOperationsGiveTheirConnectionsBack_LeavesNoConnectionOpen()
    {
        _fixture.Server.Add(Dados + "/PAMG2407.dbc", ClientFixture.Dbc("A1"));
        var leaks = 0;
        for (var round = 0; round < 5000; round++)
        {
            var before = _fixture.Server.ActiveConnections;
            var client = _fixture.Client(configure: options => options.MaxConnections = 3);
            var operations = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await client.GetInfoAsync(Dados + "/PAMG2407.dbc", Ct);
                }
                catch (ObjectDisposedException)
                {
                }
            })).ToArray();
            await Task.Yield();
            client.Dispose();
            await Task.WhenAll(operations);
            if (_fixture.Server.ActiveConnections != before)
            {
                leaks++;
            }
        }

        Assert.Equal(0, leaks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Options_InvalidConnectionCount_ThrowsArgumentOutOfRange(int connections)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DatasusClient(new DatasusClientOptions { MaxConnections = connections }));
    }

    // Each option is reported by its own name, with the value given and what it must be.
    [Theory]
    [InlineData(nameof(DatasusClientOptions.MaxRetries), "The number of retries cannot be negative.")]
    [InlineData(nameof(DatasusClientOptions.InitialRetryDelay), "InitialRetryDelay cannot be negative.")]
    [InlineData(nameof(DatasusClientOptions.ConnectTimeout), "ConnectTimeout must be positive.")]
    [InlineData(nameof(DatasusClientOptions.ReadTimeout), "ReadTimeout must be positive.")]
    [InlineData(nameof(DatasusClientOptions.LockTimeout), "LockTimeout cannot be negative.")]
    [InlineData(nameof(DatasusClientOptions.ListingCacheDuration), "ListingCacheDuration cannot be negative.")]
    public void Options_InvalidValue_NamesTheOptionThatIsWrong(string option, string message)
    {
        var options = new DatasusClientOptions();
        switch (option)
        {
            case nameof(DatasusClientOptions.MaxRetries):
                options.MaxRetries = -1;
                break;
            case nameof(DatasusClientOptions.InitialRetryDelay):
                options.InitialRetryDelay = TimeSpan.FromSeconds(-1);
                break;
            case nameof(DatasusClientOptions.ConnectTimeout):
                options.ConnectTimeout = TimeSpan.Zero;
                break;
            case nameof(DatasusClientOptions.ReadTimeout):
                options.ReadTimeout = TimeSpan.Zero;
                break;
            case nameof(DatasusClientOptions.LockTimeout):
                options.LockTimeout = TimeSpan.FromSeconds(-1);
                break;
            default:
                options.ListingCacheDuration = TimeSpan.FromSeconds(-1);
                break;
        }

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => new DatasusClient(options));

        Assert.Equal(option, exception.ParamName);
        Assert.StartsWith(message, exception.Message, StringComparison.Ordinal);
        Assert.NotNull(exception.ActualValue);
    }

    [Fact]
    public void Options_ZeroDelayAndDurations_AreAccepted()
    {
        using var client = new DatasusClient(new DatasusClientOptions
        {
            MaxRetries = 0,
            InitialRetryDelay = TimeSpan.Zero,
            LockTimeout = TimeSpan.Zero,
            ListingCacheDuration = TimeSpan.Zero,
        });

        Assert.NotNull(client);
    }
}
