using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A server that stops answering, intended to be handled on every runtime: the read timeout and the
// cancellation token both end the operation, the connection is not reused, and a download cut short
// leaves no cache entry.
// On .NET Framework, FluentFTP's asynchronous reads do not observe their timeout or the token, so
// without the client's own handling these operations would never end.
public sealed class StalledServerTests : IDisposable
{
    private const string FilePath = "/X/A.dbf";
    private const string FullPath = "/dissemin/publicos/X/A.dbf";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "dbsus-stalled-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task SilentServer_ReadTimeoutEndsTheOperation()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "SIZE";
        using var client = Client(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.GetInfoAsync(FilePath, ct));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
    }

    [Fact(Timeout = 30_000)]
    public async Task Cancellation_EndsAStuckOperationAndTheConnectionIsNotReused()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.SilentOn = "SIZE";
        using var client = Client(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInfoAsync(FilePath, cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
        _server.SilentOn = null;
        var info = await client.GetInfoAsync(FilePath, ct);

        Assert.Equal(10, info.Size);
        Assert.Equal(2, _server.ControlConnections);
    }

    [Fact(Timeout = 60_000)]
    public async Task DownloadCanceledMidTransfer_LeavesNoCacheEntryAndResumesLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Content(200_000);
        _server.Files[FullPath] = content;
        _server.StallTransferAfter = 50_000;
        using var client = Client(TimeSpan.FromSeconds(60), cache: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancellation.CancelAfter(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(FilePath, cancellation.Token));
        Assert.Empty(Directory.EnumerateFiles(_cache, "A.dbf", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_cache, "entry.meta", SearchOption.AllDirectories));
        var partial = Assert.Single(Directory.EnumerateFiles(_cache, "A.dbf.partial", SearchOption.AllDirectories));
        Assert.InRange(new FileInfo(partial).Length, 1, content.Length - 1);
        _server.StallTransferAfter = null;
        var local = await client.DownloadAsync(FilePath, ct);

        Assert.Equal(content, File.ReadAllBytes(local));
        Assert.Contains("REST", _server.Commands);
    }

    [Fact(Timeout = 60_000)]
    public async Task StalledDownload_EndsAfterTheReadTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.Files[FullPath] = Content(200_000);
        _server.StallTransferAfter = 50_000;
        using var client = Client(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.DownloadAsync(FilePath, ct));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
    }

    private static byte[] Content(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private DatasusClient Client(TimeSpan readTimeout, bool cache = false) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = 0,
        ConnectTimeout = TimeSpan.FromSeconds(1),
        ReadTimeout = readTimeout,
        CacheDirectory = cache ? _cache : null,
    });
}
