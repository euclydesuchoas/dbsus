using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

/// <summary>
/// The committed CNES fixtures (see tests/data/fixtures/README.md). Unlike the other samples they
/// must always be present, so CI decodes real compressed data: back-references, window reuse past
/// 4 KiB and copies suspended at every chunk size.
/// </summary>
public class DbcFixtureTests
{
    [Theory]
    [InlineData("EQRR0509.dbc", 1)]
    [InlineData("EQRR0509.dbc", 7)]
    [InlineData("EQRR0509.dbc", 81920)]
    [InlineData("LTRR0610.dbc", 1)]
    [InlineData("LTRR0610.dbc", 7)]
    [InlineData("LTRR0610.dbc", 81920)]
    public void OpenAsDbf_CommittedFixture_MatchesOracle(string file, int chunkSize)
    {
        var expected = ExpectedSamples.Get(file);
        var path = Path.Combine(RepositoryPaths.Fixtures, file);
        Assert.True(File.Exists(path), $"Committed fixture {file} is missing from tests/data/fixtures.");
        Assert.Equal(expected.DbcSha256, ExpectedSamples.Sha256OfFile(path));

        using var stream = DbcFile.OpenAsDbf(OpenSource(path, chunkSize));
        var digest = DbfDigest.Read(stream, chunkSize);

        Assert.Equal(expected.DbfSize, digest.Size);
        Assert.Equal(expected.DbfSha256, digest.Sha256);
        Assert.True(digest.SizeMatchesHeader);
    }

    // With one-byte output reads, also feed the input one byte at a time and without seeking.
    private static Stream OpenSource(string path, int chunkSize) =>
        chunkSize == 1 ? new TrickleStream(File.ReadAllBytes(path)) : File.OpenRead(path);
}
