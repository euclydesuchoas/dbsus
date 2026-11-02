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
    public void DatasusRemoteFileChangedException_IsASealedTransferFailureThatOnlyTheClientCreates()
    {
        var type = typeof(DatasusRemoteFileChangedException);
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(type.IsSealed);
        Assert.Equal(typeof(DatasusTransferException), type.BaseType);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsAssembly, constructor.ToString()));
        AssertReadOnlyFileInfo(type.GetProperty(nameof(DatasusRemoteFileChangedException.Expected))!);
        AssertReadOnlyFileInfo(type.GetProperty(nameof(DatasusRemoteFileChangedException.Current))!);
    }

    [Fact]
    public void DatasusClientOptions_ConstructorIsDocumented()
    {
        var documentation = File.ReadAllText(Path.ChangeExtension(typeof(DatasusClientOptions).Assembly.Location, ".xml"));

        Assert.Contains("<member name=\"M:DbSus.Client.DatasusClientOptions.#ctor\">", documentation, StringComparison.Ordinal);
    }

    [Fact]
    public void DatasusProgressInfo_IsReadOnly_AndOnlyTheClientCreatesIt()
    {
        var type = typeof(DatasusProgressInfo);
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.True(type.IsSealed);
        Assert.NotEmpty(constructors);
        Assert.All(constructors, constructor => Assert.True(constructor.IsAssembly, constructor.ToString()));
        Assert.All(type.GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void DatasusProgressStage_KeepsItsValues()
    {
        Assert.Equal(0, (int)DatasusProgressStage.Downloading);
        Assert.Equal(1, (int)DatasusProgressStage.Verifying);
        Assert.Equal(2, (int)DatasusProgressStage.FromCache);
        Assert.Equal(3, (int)DatasusProgressStage.Streaming);
    }

    [Fact]
    public void DatasusFileRequest_IsSealed_AndWhatItReadsCannotChange()
    {
        var type = typeof(DatasusFileRequest);

        Assert.True(type.IsSealed);
        Assert.Null(type.GetProperty(nameof(DatasusFileRequest.Path))!.SetMethod);
        Assert.Null(type.GetProperty(nameof(DatasusFileRequest.Table))!.SetMethod);
    }

    private static void AssertReadOnlyFileInfo(PropertyInfo property)
    {
        Assert.Equal(typeof(DatasusFileInfo), property.PropertyType);
        Assert.Null(property.SetMethod);
    }
}
