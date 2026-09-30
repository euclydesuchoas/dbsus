using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

/// <summary>
/// Real DATASUS files, compared with the hashes the oracle recorded in tests/data/expected.json.
/// Apart from the committed fixtures, the files are never committed: run tools/fetch-samples to
/// download the pinned ones into tests/data/local/. Each test skips itself when its file is absent
/// or is a different version.
/// </summary>
public class DbcSampleTests
{
    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var entry in ExpectedSamples.All)
        {
            data.Add(entry.File);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void OpenAsDbf_RealSample_MatchesOracleAndDbfInvariants(string file)
    {
        var expected = ExpectedSamples.Get(file);
        var path = new[] { RepositoryPaths.Fixtures, RepositoryPaths.LocalSamples }
            .Select(directory => Path.Combine(directory, file))
            .FirstOrDefault(File.Exists);
        if (path is null)
        {
            Assert.Skip($"{file} is not in tests/data/fixtures or tests/data/local; run tools/fetch-samples.");
        }

        if (ExpectedSamples.Sha256OfFile(path) != expected.DbcSha256)
        {
            Assert.Skip($"{file} on disk is not the version pinned in expected.json.");
        }

        using var stream = DbcFile.OpenAsDbf(path);
        var digest = DbfDigest.Read(stream, chunkSize: 81920);

        Assert.Equal(expected.DbfSize, digest.Size);
        Assert.Equal(expected.DbfSha256, digest.Sha256);
        Assert.True(digest.SizeMatchesHeader, $"DBF size {digest.Size} does not match the table header.");
    }
}
