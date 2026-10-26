using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// OpenReaderAsync downloads a table's parts one after the other. At the end of the opening, every part it returns must
// match, in size and time, what the server has when the parts before the last are checked again: a part downloaded
// before the table was republished makes the table start over.
public sealed class TableVersionTests : IDisposable
{
    private const string PartA = "/X/RDMG2407a.dbc";
    private const string PartB = "/X/RDMG2407b.dbc";
    private readonly ClientFixture _fixture = new();

    public TableVersionTests()
    {
        _fixture.Server.Add(PartA, ClientFixture.Dbc("A1", "A2"), FakeServer.Time(1));
        _fixture.Server.Add(PartB, ClientFixture.Dbc("B1"), FakeServer.Time(1));
    }

    public void Dispose() => _fixture.Dispose();

    // Part a is downloaded; then both parts are republished, before part b's transfer.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothPartsRepublishedAfterPartA_TheTableStartsOver_AndEveryPartIsTheNewVersion(bool cache)
    {
        var ct = TestContext.Current.CancellationToken;
        var republished = false;
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2 && !republished)
            {
                republished = true;
                _fixture.Server.Add(PartA, ClientFixture.Dbc("A1V2", "A2V2"), FakeServer.Time(2));
                _fixture.Server.Add(PartB, ClientFixture.Dbc("B1V2", "B2V2"), FakeServer.Time(2));
            }
        };

        Assert.Equal(["A1V2", "A2V2", "B1V2", "B2V2"], await ReadAllAsync(_fixture.Client(cache), ct));
    }

    // Control: only part b changes, and part a is the same on the server: the table as the server has it now, with no
    // start over (part a is transferred once).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyPartBRepublished_TheTableIsTheServersCurrentOne_WithoutStartingOver(bool cache)
    {
        var ct = TestContext.Current.CancellationToken;
        var republished = false;
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2 && !republished)
            {
                republished = true;
                _fixture.Server.Add(PartB, ClientFixture.Dbc("B1V2", "B2V2"), FakeServer.Time(2));
            }
        };

        Assert.Equal(["A1", "A2", "B1V2", "B2V2"], await ReadAllAsync(_fixture.Client(cache), ct));
        Assert.Equal(3, _fixture.Server.Transfers); // a once; b twice (its first transfer brought the new version)
    }

    // Part a changes at every one of part b's transfers: the restarts run out.
    [Fact]
    public async Task PartAChangesAtEveryRound_TheRestartsRunOut_AndNoTemporaryFolderIsLeft()
    {
        var ct = TestContext.Current.CancellationToken;
        var version = 1;
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer % 2 == 0)
            {
                version++;
                _fixture.Server.Add(PartA, ClientFixture.Dbc("A1V" + version), FakeServer.Time(version));
            }
        };

        var exception = await Assert.ThrowsAsync<DatasusRetriesExhaustedException>(() => ReadAllAsync(_fixture.Client(cache: false), ct));

        Assert.Equal(4, exception.Attempts);
        Assert.Contains("changed on the server", exception.Message, StringComparison.Ordinal);
        Assert.Equal(8, _fixture.Server.Transfers);
        Assert.Empty(Directory.Exists(_fixture.TempDirectory)
            ? Directory.EnumerateDirectories(_fixture.TempDirectory, "DbSus-*")
            : []);
    }

    // With a cache, the part that did not change is not downloaded again when the table starts over.
    [Fact]
    public async Task WithACache_TheTableStartsOver_AndTheUnchangedPartComesFromTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var republished = false;
        _fixture.Server.BeforeTransfer = transfer =>
        {
            if (transfer == 2 && !republished)
            {
                republished = true;
                _fixture.Server.Add(PartA, ClientFixture.Dbc("A1V2", "A2V2"), FakeServer.Time(2));
            }
        };

        Assert.Equal(["A1V2", "A2V2", "B1"], await ReadAllAsync(_fixture.Client(cache: true), ct));
        Assert.Equal(3, _fixture.Server.Transfers); // a, b, then a again; b from the cache
    }

    private static async Task<List<string>> ReadAllAsync(DatasusClient client, CancellationToken ct)
    {
        var codes = new List<string>();
        using var reader = await client.OpenReaderAsync(PartA, cancellationToken: ct);
        while (reader.Read())
        {
            codes.Add(reader.GetString(0)!.Trim());
        }

        return codes;
    }
}
