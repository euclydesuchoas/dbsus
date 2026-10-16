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

    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("abc", 0x352441C2u)]
    [InlineData("message digest", 0x20159D7Fu)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 0x4C2750BDu)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    public void Value_KnownVectors(string input, uint expected)
    {
        var crc = new Crc32();

        crc.Append(Encoding.ASCII.GetBytes(input));

        Assert.Equal(expected, crc.Value);
    }

    [Theory]
    [InlineData(0x00, 0x190A55ADu)]
    [InlineData(0xFF, 0xFF6CAB0Bu)]
    public void Value_ThirtyTwoEqualBytes_IsKnownValue(byte value, uint expected)
    {
        var data = new byte[32];
        data.AsSpan().Fill(value);
        var crc = new Crc32();

        crc.Append(data);

        Assert.Equal(expected, crc.Value);
    }

    [Fact]
    public void Append_EveryLengthUpTo1024_MatchesBothReferences()
    {
        var random = new Random(20261016);
        for (var length = 0; length <= 1024; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);
            var crc = new Crc32();

            crc.Append(data);

            Assert.Equal(ReferenceCrc32.Compute(data), crc.Value);
            Assert.Equal(ReferenceCrc32.ComputeByteWise(data), crc.Value);
        }
    }

    [Fact]
    public void Append_ArbitraryPieces_MatchesReference()
    {
        var random = new Random(4242);
        for (var round = 0; round < 200; round++)
        {
            var data = new byte[random.Next(0, 4096)];
            random.NextBytes(data);
            var crc = new Crc32();

            var offset = 0;
            while (offset < data.Length)
            {
                var piece = Math.Min(random.Next(0, 40), data.Length - offset);
                crc.Append(data.AsSpan(offset, piece));
                offset += piece;
            }

            Assert.Equal(ReferenceCrc32.Compute(data), crc.Value);
        }
    }

    [Fact]
    public void Append_AnyStartOffset_MatchesReference()
    {
        var buffer = new byte[2048];
        new Random(777).NextBytes(buffer);
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 63, 64, 65, 1000 })
        {
            for (var start = 0; start < 16; start++)
            {
                var slice = buffer.AsSpan(start, length);
                var crc = new Crc32();

                crc.Append(slice);

                Assert.Equal(ReferenceCrc32.Compute(slice.ToArray()), crc.Value);
            }
        }
    }

    [Fact]
    public void Append_EmptySpan_LeavesValueUnchanged()
    {
        var crc = new Crc32();
        crc.Append(Encoding.ASCII.GetBytes("123456789"));

        crc.Append(ReadOnlySpan<byte>.Empty);

        Assert.Equal(0xCBF43926u, crc.Value);
    }
}
