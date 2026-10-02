using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// FindPartsAsync does not rely on the listing alone: it asks the server (SIZE and MDTM, over the
// control connection) for the part after the last one listed, and for the file itself when the
// listing shows nothing, so a stale or incomplete listing cannot hide a part.
public sealed class FindPartsProbeTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ListingConfirmedByTheProbe_ReturnsTheListedPartsWithOneExtraRequest()
    {
        AddParts("ab");
        var client = _fixture.Client();

        var parts = await client.FindPartsAsync(Dados + "/PAMG2407.dbc", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc"], parts.Select(part => part.Name));
        Assert.Equal(3, _fixture.Server.InfoCalls);
    }

    [Fact]
    public async Task ListingWithoutTheLastPart_FindsItAndDropsTheListing()
    {
        AddParts("ab");
        var client = _fixture.Client();
        await client.ListAsync(Dados, Ct);
        AddParts("c");

        var parts = await client.FindPartsAsync(Dados + "/PAMG2407.dbc", Ct);
        var listing = await client.ListAsync(Dados, Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc", "PAMG2407c.dbc"], parts.Select(part => part.Name));
        Assert.Equal(3, listing.Count);
        Assert.Equal(2, _fixture.Server.ListCalls);
    }

    [Fact]
    public async Task ListingWithoutAnyPart_FindsThePartsByAsking()
    {
        _fixture.Server.Add(Dados + "/OTHER2407.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        await client.ListAsync(Dados, Ct);
        AddParts("ab");

        var parts = await client.FindPartsAsync(Dados + "/PAMG2407a.dbc", Ct);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc"], parts.Select(part => part.Name));
    }

    [Fact]
    public async Task ListingWithoutTheUnsplitFile_FindsItByAsking()
    {
        _fixture.Server.Add(Dados + "/OTHER2407.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client();
        await client.ListAsync(Dados, Ct);
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));

        var parts = await client.FindPartsAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(["RDMG2407.dbc"], parts.Select(part => part.Name));
    }

    [Fact]
    public async Task NothingOnTheServer_StillThrowsFileNotFound()
    {
        _fixture.Server.Add(Dados + "/OTHER2407.dbc", ClientFixture.Dbc("A1"));

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => _fixture.Client().FindPartsAsync(Dados + "/PAMG2407.dbc", Ct));

        Assert.Equal(Dados + "/PAMG2407.dbc", exception.FileName);
    }

    [Fact]
    public async Task UnsplitFile_IsNotProbed()
    {
        _fixture.Server.Add(Dados + "/RDMG2407.dbc", ClientFixture.Dbc("A1"));

        await _fixture.Client().FindPartsAsync(Dados + "/RDMG2407.dbc", Ct);

        Assert.Equal(1, _fixture.Server.InfoCalls);
    }

    private void AddParts(string letters)
    {
        foreach (var letter in letters)
        {
            _fixture.Server.Add($"{Dados}/PAMG2407{letter}.dbc", ClientFixture.Dbc("A1"));
        }
    }
}
