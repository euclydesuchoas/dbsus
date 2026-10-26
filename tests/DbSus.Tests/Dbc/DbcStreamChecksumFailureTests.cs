using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

// The stream of DbcFile.OpenAsDbf, after the read that found a checksum that does not match: every later read throws
// that same exception again, as DbfReader does, instead of reporting a clean end of the file.
public sealed class DbcStreamChecksumFailureTests
{
    [Fact]
    public void ChecksumMismatch_EveryLaterReadThrowsTheSameException()
    {
        var dbc = SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true), checksum: 0);
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(dbc));
        var buffer = new byte[4096];

        var first = Assert.Throws<InvalidDataException>(() =>
        {
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        });

        Assert.Same(first, Assert.Throws<InvalidDataException>(() => stream.Read(buffer, 0, buffer.Length)));
        Assert.Same(first, Assert.Throws<InvalidDataException>(() => stream.Read(buffer, 0, 1)));
    }

    [Fact]
    public void ChecksumMatches_TheEndStaysAClean0()
    {
        var dbc = SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(withEndOfFileMarker: true));
        using var stream = DbcFile.OpenAsDbf(new MemoryStream(dbc));
        var buffer = new byte[4096];
        while (stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }

        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }
}
