using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests;

public sealed class FindPartsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dbsus-tests", Guid.NewGuid().ToString("N"));

    public FindPartsTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("PAMG2407a.dbc")]
    [InlineData("PAMG2407c.dbc")]
    [InlineData("PAMG2407.dbc")]
    [InlineData("pamg2407B.DBC")]
    public void FindParts_FromAnyPartOrTheUnsplitName_ReturnsAllPartsInOrder(string input)
    {
        Touch("PAMG2407b.dbc", "PAMG2407a.dbc", "PAMG2407c.dbc");

        var parts = DbSusFile.FindParts(Path.Combine(_directory, input));

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc", "PAMG2407c.dbc"], parts.Select(Path.GetFileName));
        Assert.All(parts, part => Assert.True(Path.IsPathRooted(part)));
    }

    [Fact]
    public void FindParts_UpperCaseLettersAndExtensions_AreTreatedLikeLowerCase()
    {
        Touch("PAMG2407A.DBC", "PAMG2407b.dbc");

        var parts = DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc"));

        Assert.Equal(["PAMG2407A.DBC", "PAMG2407b.dbc"], parts.Select(Path.GetFileName));
    }

    [Fact]
    public void FindParts_UnsplitTable_ReturnsItsSingleFile()
    {
        Touch("RDMG2407.dbc");

        var parts = DbSusFile.FindParts(Path.Combine(_directory, "RDMG2407.dbc"));

        Assert.Equal(["RDMG2407.dbc"], parts.Select(Path.GetFileName));
    }

    [Fact]
    public void FindParts_GapInTheLetters_ThrowsFileNotFoundForTheMissingPart()
    {
        Touch("PAMG2407a.dbc", "PAMG2407c.dbc");

        var exception = Assert.Throws<FileNotFoundException>(() => DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc")));

        Assert.Equal("PAMG2407b.dbc", Path.GetFileName(exception.FileName));
    }

    [Fact]
    public void FindParts_FirstPartMissing_ThrowsFileNotFoundForPartA()
    {
        Touch("PAMG2407b.dbc", "PAMG2407c.dbc");

        var exception = Assert.Throws<FileNotFoundException>(() => DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407b.dbc")));

        Assert.Equal("PAMG2407a.dbc", Path.GetFileName(exception.FileName));
    }

    [Fact]
    public void FindParts_UnsplitAndLetteredFilesTogether_ThrowsIOException()
    {
        Touch("PAMG2407.dbc", "PAMG2407a.dbc", "PAMG2407b.dbc");

        Assert.Throws<IOException>(() => DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc")));
    }

    [Fact]
    public void FindParts_NamesDifferingOnlyByCase_ThrowsIOExceptionOnCaseSensitiveFileSystems()
    {
        Touch("PAMG2407a.dbc");
        if (File.Exists(Path.Combine(_directory, "PAMG2407A.dbc")))
        {
            Assert.Skip("The file system is case-insensitive, so both names cannot exist.");
        }

        Touch("PAMG2407A.dbc");

        Assert.Throws<IOException>(() => DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc")));
    }

    [Fact]
    public void FindParts_IgnoresOtherExtensionsAndLongerNames()
    {
        Touch("PAMG2407a.dbc", "PAMG2407b.dbf", "PAMG24071a.dbc", "PAMG2407ab.dbc", "PAMG2408a.dbc");

        var parts = DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc"));

        Assert.Equal(["PAMG2407a.dbc"], parts.Select(Path.GetFileName));
    }

    [Fact]
    public void FindParts_NothingFound_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => DbSusFile.FindParts(Path.Combine(_directory, "PAMG2407a.dbc")));
    }

    [Theory]
    [InlineData("readme.dbc")]
    [InlineData("PAMG2407ab.dbc")]
    public void FindParts_NameOutsideDatasusConvention_ThrowsArgumentException(string name)
    {
        Assert.Throws<ArgumentException>(() => DbSusFile.FindParts(Path.Combine(_directory, name)));
    }

    [Fact]
    public void FindParts_LocalPaMgSample_FindsBothParts()
    {
        var first = Path.Combine(RepositoryPaths.LocalSamples, "PAMG2407a.dbc");
        if (!File.Exists(first))
        {
            Assert.Skip("PAMG2407a.dbc is not in tests/data/local.");
        }

        var parts = DbSusFile.FindParts(first);

        Assert.Equal(["PAMG2407a.dbc", "PAMG2407b.dbc"], parts.Select(Path.GetFileName));
    }

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(_directory, name), []);
        }
    }
}
