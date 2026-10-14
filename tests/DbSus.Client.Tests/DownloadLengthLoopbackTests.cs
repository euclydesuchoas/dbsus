using System.Diagnostics;
using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The download limit (see DownloadLengthTests) over FluentFTP and a real socket: a server that
// sends more than its SIZE, or sends without end, fails the attempt quickly, without the stream
// being read to its end or hanging when it is closed; the .partial is deleted, nothing beyond the
// size is ever on disk, and the next attempt asks the server for the size and the time again.
public sealed class DownloadLengthLoopbackTests : IDisposable
{
    private const string FilePath = "/X/A.dbf";
    private const string FullPath = "/dissemin/publicos/X/A.dbf";
    private const int Size = 100_000;
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "dbsus-length-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _checks = [];

    public DownloadLengthLoopbackTests()
    {
        _server.Files[FullPath] = [.. Enumerable.Range(0, Size).Select(i => (byte)(i % 251))];
    }

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

    [Theory(Timeout = 60_000)]
    [InlineData(1_000L)]
    [InlineData(50_000_000L)]
    [InlineData(long.MaxValue)]
    public async Task ServerSendsMoreThanItsSize_EveryAttemptFailsQuickly_AndNothingIsKept(long extra)
    {
        var ct = TestContext.Current.CancellationToken;
        _server.ExtraBytesAfterFile = extra;
        using var client = Client(retries: 1);
        var watch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.DownloadAsync(FilePath, ct));

        watch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine($"{extra} extra bytes: failed after {watch.Elapsed.TotalMilliseconds:N0} ms, {_server.ExtraBytesSent:N0} extra bytes sent.");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        Assert.Equal(2, exception.Attempts);
        Assert.Contains("sent more than", exception.InnerException!.Message, StringComparison.Ordinal);
        Assert.All(_checks, check => Assert.Equal("no file beyond the size, no .partial", check));
        Assert.Single(_checks);
        Assert.Empty(Directory.Exists(_cache) ? Directory.EnumerateFiles(_cache, "A.dbf*", SearchOption.AllDirectories) : []);
        if (extra == long.MaxValue)
        {
            // A server that never stops: the stream was closed long before anything like its end.
            Assert.True(_server.ExtraBytesSent < 1_000_000_000, _server.ExtraBytesSent.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(["SIZE", "MDTM", "RETR", "SIZE", "MDTM", "RETR"], Transfers());
    }

    [Theory(Timeout = 60_000)]
    [InlineData(1_000L)]
    [InlineData(long.MaxValue)]
    public async Task ServerSendsMoreOnce_TheNextAttemptAsksAgainAndDownloadsTheFile(long extra)
    {
        var ct = TestContext.Current.CancellationToken;
        _server.ExtraBytesAfterFile = extra;
        using var client = Client(retries: 1, onRetry: () => _server.ExtraBytesAfterFile = null);

        var path = await client.DownloadAsync(FilePath, ct);

        Assert.Equal(_server.Files[FullPath], File.ReadAllBytes(path));
        Assert.Equal(["no file beyond the size, no .partial"], _checks);
        Assert.Equal(["SIZE", "MDTM", "RETR", "SIZE", "MDTM", "RETR", "SIZE", "MDTM"], Transfers());
    }

    [Fact(Timeout = 60_000)]
    public async Task ServerSendsExactlyTheSize_OneTransfer()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Client(retries: 1);

        var path = await client.DownloadAsync(FilePath, ct);

        Assert.Equal(_server.Files[FullPath], File.ReadAllBytes(path));
        Assert.Empty(_checks);
        Assert.Equal(["SIZE", "MDTM", "RETR", "SIZE", "MDTM"], Transfers());
    }

    // The commands that ask for the size and the time and that transfer, in order.
    private string[] Transfers() => [.. _server.Commands.Where(command => command is "SIZE" or "MDTM" or "RETR")];

    // Before each retry, what is on disk: no file of the download longer than the size, and no .partial.
    private DatasusClient Client(int retries, Action? onRetry = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        CacheDirectory = _cache,
        MaxRetries = retries,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReadTimeout = TimeSpan.FromSeconds(5),
        OnRetry = _ =>
        {
            var files = Directory.Exists(_cache) ? Directory.GetFiles(_cache, "A.dbf*", SearchOption.AllDirectories) : [];
            var tooLong = files.Any(file => new FileInfo(file).Length > Size);
            var partial = files.Any(file => file.EndsWith(".partial", StringComparison.Ordinal));
            _checks.Add(tooLong || partial ? $"too long: {tooLong}, partial: {partial}" : "no file beyond the size, no .partial");
            onRetry?.Invoke();
        },
    });
}
