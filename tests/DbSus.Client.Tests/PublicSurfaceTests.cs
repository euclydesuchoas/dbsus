using System.Reflection;
using Xunit;

namespace DbSus.Client.Tests;

// Shapes of the client's public API decided in the API review before the first preview.
public class PublicSurfaceTests
{
    [Fact]
    public void DatasusTransferException_IsAnAbstractBaseThatOnlyTheClientCanDeriveFrom()
    {
        var type = typeof(DatasusTransferException);
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(type.IsAbstract);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsFamilyAndAssembly, constructor.ToString()));
        Assert.True(typeof(DatasusRetriesExhaustedException).IsSubclassOf(type));
        Assert.True(typeof(DatasusRequestRefusedException).IsSubclassOf(type));
    }

    [Fact]
    public void DatasusClientOptions_ConstructorIsDocumented()
    {
        var documentation = File.ReadAllText(Path.ChangeExtension(typeof(DatasusClientOptions).Assembly.Location, ".xml"));

        Assert.Contains("<member name=\"M:DbSus.Client.DatasusClientOptions.#ctor\">", documentation, StringComparison.Ordinal);
    }
}
