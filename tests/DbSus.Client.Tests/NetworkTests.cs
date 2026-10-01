using System.Security.Cryptography;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Client.Tests;

/// <summary>
/// Against the real DATASUS FTP server. Skipped unless the environment variable
/// <c>DBSUS_NETWORK_TESTS</c> is <c>1</c>, so CI never depends on the network; a separate, manual or
/// weekly workflow runs them without blocking anything. They touch only small, stable files and
/// use one connection.
/// </summary>
public sealed class NetworkTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "dbsus-network-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListAsync_SihDados_ContainsAKnownFile()
    {
        using var client = Client();

        var entries = await client.ListAsync("/SIHSUS/200801_/Dados", Ct);

        Assert.Contains(entries, entry => entry.Name == "RDRR2206.dbc" && !entry.IsDirectory);
    }

    [Fact]
    public async Task GetInfoAsync_KnownFile_HasExactSize()
    {
        using var client = Client();

        var info = await client.GetInfoAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", Ct);

        Assert.Equal(50_318, info.Size);
        Assert.Equal(DateTimeKind.Utc, info.LastModifiedUtc!.Value.Kind);
    }

    [Fact]
    public async Task ReadHeaderAsync_KnownFile_GivesTheRecordCount()
    {
        using var client = Client();

        var header = await client.ReadHeaderAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", Ct);

        Assert.Equal(668, header.RecordCount);
        Assert.Equal(113, header.Fields.Count);
    }

    [Fact]
    public async Task FindPartsAsync_PaMg202407_HasTwoParts()
    {
        using var client = Client();

        var parts = await client.FindPartsAsync("/SIASUS/200801_/Dados/PAMG2407.dbc", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc"], parts.Select(part => part.Name));
    }

    [Fact]
    public async Task DownloadAsync_CnesFixture_MatchesTheCommittedCopy()
    {
        using var client = Client();

        var local = await client.DownloadAsync("/CNES/200508_/Dados/EQ/EQRR0509.dbc", Ct);

        Assert.Equal(Sha256(Path.Combine(RepositoryPaths.Fixtures, "EQRR0509.dbc")), Sha256(local));
    }

    [Fact]
    public async Task OpenReaderAsync_CnesFixture_ReadsEveryRecord()
    {
        using var client = Client();

        using var reader = await client.OpenReaderAsync("/CNES/200508_/Dados/LT/LTRR0610.dbc", cancellationToken: Ct);
        var records = 0;
        while (reader.Read())
        {
            records++;
        }

        Assert.Equal(102, records);
    }

    private DatasusClient Client()
    {
        if (Environment.GetEnvironmentVariable("DBSUS_NETWORK_TESTS") != "1")
        {
            Assert.Skip("Network tests run only with DBSUS_NETWORK_TESTS=1.");
        }

        return new DatasusClient(new DatasusClientOptions { CacheDirectory = _cache, MaxConnections = 1 });
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        return Convert.ToBase64String(sha256.ComputeHash(stream));
    }
}
