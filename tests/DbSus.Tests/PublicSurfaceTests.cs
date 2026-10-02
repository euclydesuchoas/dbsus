using System.Reflection;
using DbSus.Dbf;
using Xunit;

namespace DbSus.Tests;

// Members deliberately kept out of the public API (decided in the API review before the first
// preview). Tests reach them through InternalsVisibleTo.
public class PublicSurfaceTests
{
    [Fact]
    public void DbfReader_OpensOnlyASingleFileOrStreamPublicly()
    {
        var opens = typeof(DbfReader).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(DbfReader.Open))
            .Select(method => method.GetParameters()[0].ParameterType.Name)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(["Stream", "String"], opens);
    }

    [Fact]
    public void DbSusFile_OpensDbcStreamsPubliclyOnlyAsOneStreamOrAsDbcParts()
    {
        var overloads = typeof(DbSusFile).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(DbSusFile.OpenDbcReader))
            .Select(method => method.GetParameters()[0].ParameterType.ToString())
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(
            [
                "System.Collections.Generic.IEnumerable`1[DbSus.Dbc.DbcPart]",
                "System.Collections.Generic.IEnumerable`1[System.String]",
                "System.IO.Stream",
                "System.String",
            ],
            overloads);
    }

    [Fact]
    public void DbfHeader_UnusedRecordBytes_IsNotPublic()
    {
        Assert.Null(typeof(DbfHeader).GetProperty("UnusedRecordBytes"));
    }

    [Fact]
    public void DbfField_Conversion_IsNotPublic()
    {
        Assert.Null(typeof(DbfField).GetProperty("Conversion"));
    }
}
