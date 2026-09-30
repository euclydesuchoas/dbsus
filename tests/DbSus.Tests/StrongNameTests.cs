using System.Globalization;
using Xunit;

namespace DbSus.Tests;

public class StrongNameTests
{
    // The strong-name key must never change: a new key is a new assembly identity and breaks
    // every compiled consumer. This pins the public key token of the committed DbSus.snk.
    private const string ExpectedPublicKeyToken = "31b7992898fa311e";

    [Fact]
    public void DbSusAssembly_IsSigned_WithCommittedKey()
    {
        var name = typeof(DbSusAssemblyMarker).Assembly.GetName();

        var token = name.GetPublicKeyToken();

        Assert.NotNull(token);
        Assert.Equal(ExpectedPublicKeyToken, string.Concat(token!.Select(b => b.ToString("x2", CultureInfo.InvariantCulture))));
    }
}
