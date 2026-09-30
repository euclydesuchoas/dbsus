using System.Text;
using DbSus.Dbc;
using DbSus.Tests.TestSupport;
using Xunit;

namespace DbSus.Tests.Dbc;

public class Crc32Tests
{
    [Fact]
    public void Value_StandardCheckInput_IsCbf43926()
    {
        // "123456789" is the standard check input for CRC-32/ISO-HDLC (the zlib CRC).
        var crc = new Crc32();

        crc.Append(Encoding.ASCII.GetBytes("123456789"));

        Assert.Equal(0xCBF43926u, crc.Value);
    }

    [Fact]
    public void Value_NoInput_IsZero()
    {
        Assert.Equal(0u, new Crc32().Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Append_InChunks_MatchesReferenceImplementation(int chunkSize)
    {
        var data = new byte[10_000];
        new Random(1234).NextBytes(data);
        var crc = new Crc32();

        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            crc.Append(data.AsSpan(offset, Math.Min(chunkSize, data.Length - offset)));
        }

        Assert.Equal(ReferenceCrc32.Compute(data), crc.Value);
    }
}
