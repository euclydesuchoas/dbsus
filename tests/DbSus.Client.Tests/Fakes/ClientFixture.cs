using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests.Fakes;

/// <summary>
/// A fake server, a temporary cache and temp folder, a controllable clock, and a client wired to
/// them with no real delays between retries.
/// </summary>
internal sealed class ClientFixture : IDisposable
{
    private readonly List<DatasusClient> _clients = [];

    public ClientFixture()
    {
        Directory.CreateDirectory(Workspace);
    }

    public FakeServer Server { get; } = new();

    public string Workspace { get; } = Path.Combine(Path.GetTempPath(), "dbsus-client-tests", Guid.NewGuid().ToString("N"));

    public string CacheDirectory => Path.Combine(Workspace, "cache");

    public string TempDirectory => Path.Combine(Workspace, "temp");

    public DateTime Now { get; set; } = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    public long? FreeSpace { get; set; }

    /// <summary>Backoff delays the client asked for (they are not actually waited).</summary>
    public System.Collections.Concurrent.ConcurrentQueue<TimeSpan> Delays { get; } = new();

    public static byte[] Dbf(params string[] codes)
    {
        var builder = new DbfBuilder().Field("CODE", 'C', 4);
        foreach (var code in codes)
        {
            builder.Record(code);
        }

        return builder.Build();
    }

    public static byte[] Dbc(params string[] codes) => SyntheticDbc.BuildDbc(Dbf(codes));

    public static byte[] CorruptDbc(params string[] codes) => SyntheticDbc.BuildDbc(Dbf(codes), checksum: 0);

    public DatasusClient Client(bool cache = true, Action<DatasusClientOptions>? configure = null)
    {
        var options = new DatasusClientOptions
        {
            CacheDirectory = cache ? CacheDirectory : null,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
        };
        configure?.Invoke(options);
        var client = new DatasusClient(
            options,
            Server.Factory(),
            (delay, token) =>
            {
                Delays.Enqueue(delay);
                return Task.CompletedTask;
            },
            _ => FreeSpace,
            () => Now,
            TempDirectory);
        _clients.Add(client);
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            // The clean-up of the temporary folders runs on the thread pool: it ends before the
            // workspace goes.
            Assert.True(client.TempCleanup.Wait(TimeSpan.FromSeconds(30)), "The clean-up of the temporary folders did not end.");
            client.Dispose();
        }

        try
        {
            Directory.Delete(Workspace, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
