using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// Without a cache, OpenReaderAsync downloads a table's parts one after the other, each into a folder
// of its own under the temporary directory. When a later part fails for good (refused, retries
// exhausted, or cancelled), the parts already downloaded go too, with their folders, and the
// exception is the part's own. Over FluentFTP and a real socket, with a temporary directory of the
// test's own.
public sealed class PartDownloadFailureLoopbackTests : IDisposable
{
    private const string Folder = "/SIASUS/200801_/Dados";
    private readonly LoopbackFtpServer _server = new() { AdvertiseEpsv = false };
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dbsus-part-failure-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<char, byte[]> _parts = new()
    {
        ['a'] = ClientFixture.Dbc("A1", "A2"),
        ['b'] = ClientFixture.Dbc("B1"),
        ['c'] = ClientFixture.Dbc("C1", "C2", "C3"),
    };

    public PartDownloadFailureLoopbackTests()
    {
        Directory.CreateDirectory(_temp);
        _server.Listing = [.. _parts.Select(part => LoopbackFtpServer.FileLine($"PAMG2407{part.Key}.dbc", part.Value.Length))];
        foreach (var part in _parts)
        {
            _server.Files[Full(part.Key)] = part.Value;
        }
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory(Timeout = 60_000)]
    [InlineData('b')]
    [InlineData('c')]
    public async Task LaterPartRefused_ItsRefusalComesOut_AndNoDownloadedPartIsLeft(char failing)
    {
        var ct = TestContext.Current.CancellationToken;
        _server.RetrReplies[Full(failing)] = "553 Requested action not taken.";
        using var client = Client(retries: 1);

        var exception = await Assert.ThrowsAsync<DatasusRequestRefusedException>(() => client.OpenReaderAsync(Folder + "/PAMG2407a.dbc", cancellationToken: ct));

        Assert.Equal("553", exception.FtpReplyCode);
        await AssertNothingLeftAsync(client, failing, ct);
    }

    [Theory(Timeout = 60_000)]
    [InlineData('b')]
    [InlineData('c')]
    public async Task LaterPartFailsAtEveryAttempt_RetriesExhaustedComesOut_AndNoDownloadedPartIsLeft(char failing)
    {
        var ct = TestContext.Current.CancellationToken;
        _server.RetrReplies[Full(failing)] = "451 Local error in processing.";
        using var client = Client(retries: 1);

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => client.OpenReaderAsync(Folder + "/PAMG2407a.dbc", cancellationToken: ct));

        Assert.Equal(2, exception.Attempts);
        await AssertNothingLeftAsync(client, failing, ct);
    }

    // The caller cancels while part b waits for its next attempt.
    [Fact(Timeout = 60_000)]
    public async Task CancelledDuringPartB_TheCancellationComesOut_AndNoDownloadedPartIsLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        _server.RetrReplies[Full('b')] = "451 Local error in processing.";
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var client = Client(retries: 4, onRetry: cancel.Cancel, retryDelay: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OpenReaderAsync(Folder + "/PAMG2407a.dbc", cancellationToken: cancel.Token));

        await AssertNothingLeftAsync(client, 'b', ct);
    }

    private static string Full(char letter) => $"/dissemin/publicos{Folder}/PAMG2407{letter}.dbc";

    // The parts before the failing one were transferred, and nothing of any download is left in the
    // temporary directory: no folder of a download, no file.
    private async Task AssertNothingLeftAsync(DatasusClient client, char failing, CancellationToken ct)
    {
        Assert.True(await Task.WhenAny(client.TempCleanup, Task.Delay(TimeSpan.FromSeconds(30), ct)) == client.TempCleanup, "The clean-up of the temporary folders did not end.");
        var transferred = _server.Commands.Count(command => command == "RETR");
        Assert.True(transferred > failing - 'a', $"{transferred} transfers: the parts before {failing} were not all downloaded.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp, "*", SearchOption.AllDirectories));
    }

    private DatasusClient Client(int retries, Action? onRetry = null, TimeSpan? retryDelay = null) => new(
        new DatasusClientOptions
        {
            Host = "127.0.0.1",
            Port = _server.Port,
            MaxRetries = retries,
            InitialRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(10),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            ReadTimeout = TimeSpan.FromSeconds(5),
            OnRetry = _ => onRetry?.Invoke(),
        },
        null,
        null,
        null,
        null,
        _temp);
}
