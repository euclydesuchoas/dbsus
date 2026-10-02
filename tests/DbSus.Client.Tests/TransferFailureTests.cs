using DbSus.Client.Tests.Fakes;
using DbSus.Client.Transport;
using Xunit;

namespace DbSus.Client.Tests;

// Every public operation, failing while connecting or on an open connection, transiently or by a
// refusal: the exception always names the path the operation concerned.
public sealed class TransferFailureTests : IDisposable
{
    private const string Dados = "/SIASUS/200801_/Dados";
    private const string File = Dados + "/PAMG2407a.dbc";
    private readonly ClientFixture _fixture = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<string, string, bool, bool> Cases()
    {
        var data = new TheoryData<string, string, bool, bool>();
        (string Operation, string Expected)[] operations =
        [
            ("List", Dados),
            ("ListRoot", "/"),
            ("GetInfo", File),
            ("FindParts", Dados),
            ("ReadHeader", File),
            ("Download", File),
            ("OpenReader", Dados),
        ];
        foreach (var (operation, expected) in operations)
        {
            foreach (var whileConnecting in new[] { true, false })
            {
                foreach (var refused in new[] { true, false })
                {
                    data.Add(operation, expected, whileConnecting, refused);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RemotePath_IsAlwaysSet(string operation, string expected, bool whileConnecting, bool refused)
    {
        _fixture.Server.Add(File, ClientFixture.Dbc("A1"));
        Func<Exception> failure = refused
            ? () => new DatasusRequestRefusedException("530 Not logged in", "530", null)
            : () => new TransientTransportException("421 Too many connections", "421");
        if (whileConnecting)
        {
            _fixture.Server.FailNextConnects(10, failure);
        }
        else
        {
            _fixture.Server.FailNextOperations(10, failure);
        }

        var client = _fixture.Client(configure: options => options.MaxRetries = 1);

        var exception = await Assert.ThrowsAnyAsync<DatasusTransferException>(() => Run(client, operation));

        Assert.Equal(expected, exception.RemotePath);
        Assert.Equal(refused ? 1 : 2, exception.Attempts);
    }

#if NET
    [Fact]
    public void RemotePath_IsDeclaredNonNullable()
    {
        var property = typeof(DatasusTransferException).GetProperty(nameof(DatasusTransferException.RemotePath))!;

        Assert.Equal(System.Reflection.NullabilityState.NotNull, new System.Reflection.NullabilityInfoContext().Create(property).ReadState);
    }
#endif

    private static async Task Run(DatasusClient client, string operation)
    {
        switch (operation)
        {
            case "List":
                await client.ListAsync(Dados, Ct);
                break;
            case "ListRoot":
                await client.ListAsync("/", Ct);
                break;
            case "GetInfo":
                await client.GetInfoAsync(File, Ct);
                break;
            case "FindParts":
                await client.FindPartsAsync(File, Ct);
                break;
            case "ReadHeader":
                await client.ReadHeaderAsync(File, Ct);
                break;
            case "Download":
                await client.DownloadAsync(File, Ct);
                break;
            default:
                using (await client.OpenReaderAsync(File, cancellationToken: Ct))
                {
                }

                break;
        }
    }
}
