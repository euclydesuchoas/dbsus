using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// A transfer that ends inside the header of a file the server announces as longer, over FluentFTP
// and a real socket, with a 426, a 226 or no final reply (FluentFTP then waits 10 s for one before
// the next command): a transfer cut short, retried as any other, never a header taken for invalid.
// A file that really ends inside its header is invalid, and is not retried.
public sealed class HeaderTruncationLoopbackTests : IDisposable
{
    private const string FilePath = "/SIHSUS/200801_/Dados/RDRR2206.dbc";
    private const string FullPath = "/dissemin/publicos" + FilePath;
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private int _retries;

    public void Dispose() => _server.Dispose();

    // 20 bytes end inside the first 32 of the header; 50, inside the rest (it takes 65).
    [Theory(Timeout = 30_000)]
    [InlineData(20, "426 Connection closed; transfer aborted.")]
    [InlineData(20, "226 Transfer complete.")]
    [InlineData(50, "426 Connection closed; transfer aborted.")]
    [InlineData(50, "226 Transfer complete.")]
    [InlineData(50, null)]
    public async Task ReadHeaderAsync_TransferEndsInsideTheHeaderOfALongerFile_IsRetried(long cut, string? reply)
    {
        _server.Files[FullPath] = ClientFixture.Dbc("A1", "B2");
        _server.EndTransferAfter = cut;
        _server.EndedTransferReply = reply;
        using var client = Client(onRetry: () => _server.EndTransferAfter = null);

        var header = await client.ReadHeaderAsync(FilePath, TestContext.Current.CancellationToken);

        Assert.Equal(2, header.RecordCount);
        Assert.Equal(1, Volatile.Read(ref _retries));
        Assert.Equal(2, _server.Commands.Count(command => command == "RETR"));
    }

    [Theory(Timeout = 30_000)]
    [InlineData(20)]
    [InlineData(50)]
    public async Task ReadHeaderAsync_FileEndsInsideItsHeader_IsInvalidAndNotRetried(int length)
    {
        _server.Files[FullPath] = [.. ClientFixture.Dbc("A1", "B2").Take(length)];
        using var client = Client();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadHeaderAsync(FilePath, TestContext.Current.CancellationToken));

        Assert.Contains("ends inside its header", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _retries));
        Assert.Single(_server.Commands, command => command == "RETR");
    }

    private DatasusClient Client(Action? onRetry = null) => new(new DatasusClientOptions
    {
        Host = "127.0.0.1",
        Port = _server.Port,
        MaxRetries = 2,
        InitialRetryDelay = TimeSpan.FromMilliseconds(10),
        ConnectTimeout = TimeSpan.FromSeconds(2),
        ReadTimeout = TimeSpan.FromSeconds(5),
        OnRetry = _ =>
        {
            Interlocked.Increment(ref _retries);
            onRetry?.Invoke();
        },
    });
}
