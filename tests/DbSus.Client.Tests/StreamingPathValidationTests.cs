using DbSus.Client.Tests.Fakes;
using Xunit;

namespace DbSus.Client.Tests;

// The path given to OpenStreamingReaderAsync is checked before the reader takes one of the places
// of streaming readers: a path that is not acceptable throws the ArgumentException of
// OpenReaderAsync, even with MaxConnections = 1 or with every place taken, and nothing is sent.
public sealed class StreamingPathValidationTests : IDisposable
{
    private const string Split = "/SIASUS/200801_/Dados/PAMG2407.dbc";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData("/SIASUS\\PAMG2407.dbc")]
    [InlineData("/SIASUS/../PAMG2407.dbc")]
    [InlineData("ftp://host/PAMG2407.dbc")]
    [InlineData("")]
    public async Task OneConnection_AnInvalidPathThrowsArgumentException_AsOpenReaderAsync(string path)
    {
        var client = _fixture.Client(cache: false, configure: options => options.MaxConnections = 1);

        var expected = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenReaderAsync(path, cancellationToken: Ct));
        var actual = await Assert.ThrowsAsync<ArgumentException>(() => client.OpenStreamingReaderAsync(path, cancellationToken: Ct));

        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(0, _fixture.Server.ConnectionsOpened);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task OneConnection_ANullPathThrowsArgumentNullException()
    {
        var client = _fixture.Client(cache: false, configure: options => options.MaxConnections = 1);

        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() => client.OpenStreamingReaderAsync((string)null!, cancellationToken: Ct));

        Assert.Equal("path", exception.ParamName);
        Assert.Equal(0, client.OpenStreamingReaders);
    }

    [Fact]
    public async Task EveryPlaceTaken_AnInvalidPathThrowsArgumentException_AndTakesNoPlace()
    {
        _fixture.Server.Add("/SIASUS/200801_/Dados/PAMG2407a.dbc", ClientFixture.Dbc("A1"));
        var client = _fixture.Client(cache: false);
        using var first = await client.OpenStreamingReaderAsync(Split, cancellationToken: Ct);
        var before = (_fixture.Server.ConnectionsOpened, _fixture.Server.Transfers, _fixture.Server.ListCalls);

        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenStreamingReaderAsync("/SIASUS\\PAMG2407.dbc", cancellationToken: Ct));

        Assert.Equal(before, (_fixture.Server.ConnectionsOpened, _fixture.Server.Transfers, _fixture.Server.ListCalls));
        Assert.Equal(1, client.OpenStreamingReaders);
        Assert.True(first.Read());
    }
}
