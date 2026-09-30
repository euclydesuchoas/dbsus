using System.Text;
using DbSus.Dbc.Implode;
using Xunit;

namespace DbSus.Tests.Dbc.Implode;

public class BlastDecoderTests
{
    // Upstream test vector from zlib contrib/blast (test/test.pk -> test/test.txt, zlib license).
    // Raw literals, 1 KiB dictionary: "AI" as two literals, then a copy of length 11 at distance 2,
    // then the end code. The copy overlaps its own output, which exercises the resumable copy.
    private static readonly byte[] UpstreamVector = [0x00, 0x04, 0x82, 0x24, 0x25, 0x8f, 0x80, 0x7f];
    private const string UpstreamText = "AIAIAIAIAIAIA";

    [Fact]
    public void Read_UpstreamVector_ProducesExpectedText()
    {
        var decoder = new BlastDecoder(new MemoryStream(UpstreamVector));

        var output = ReadAll(decoder, chunkSize: 4096);

        Assert.Equal(UpstreamText, Encoding.ASCII.GetString(output));
        Assert.True(decoder.IsFinished);
        Assert.Equal(UpstreamVector.Length, decoder.InputBytesConsumed);
        Assert.Equal(UpstreamText.Length, decoder.OutputBytesProduced);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Read_UpstreamVectorInSmallChunks_ResumesMidCopy(int chunkSize)
    {
        var decoder = new BlastDecoder(new MemoryStream(UpstreamVector));

        var output = ReadAll(decoder, chunkSize);

        Assert.Equal(UpstreamText, Encoding.ASCII.GetString(output));
        Assert.True(decoder.IsFinished);
    }

    [Fact]
    public void Read_OneByteAtATimeFromOneByteInput_ProducesExpectedText()
    {
        var decoder = new BlastDecoder(new TrickleStream(UpstreamVector), inputBufferSize: 1);

        var output = ReadAll(decoder, chunkSize: 1);

        Assert.Equal(UpstreamText, Encoding.ASCII.GetString(output));
        Assert.Equal(UpstreamVector.Length, decoder.InputBytesConsumed);
    }

    [Fact]
    public void Read_AfterEndOfStream_ReturnsZero()
    {
        var decoder = new BlastDecoder(new MemoryStream(UpstreamVector));
        ReadAll(decoder, chunkSize: 4096);

        var read = decoder.Read(new byte[16]);

        Assert.Equal(0, read);
    }

    [Fact]
    public void Read_EmptyDestination_ReturnsZeroWithoutConsumingInput()
    {
        var decoder = new BlastDecoder(new MemoryStream(UpstreamVector));

        var read = decoder.Read(Span<byte>.Empty);

        Assert.Equal(0, read);
        Assert.Equal(0, decoder.InputBytesConsumed);
        Assert.False(decoder.IsFinished);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void Read_TruncatedInput_ThrowsInvalidDataException(int length)
    {
        var decoder = new BlastDecoder(new MemoryStream(UpstreamVector, 0, length));

        Assert.Throws<InvalidDataException>(() => ReadAll(decoder, chunkSize: 4096));
    }

    [Fact]
    public void Read_InvalidLiteralFlag_ThrowsInvalidDataException()
    {
        var decoder = new BlastDecoder(new MemoryStream([0x02, 0x04, 0x00]));

        Assert.Throws<InvalidDataException>(() => decoder.Read(new byte[16]));
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x07)]
    public void Read_InvalidDictionarySize_ThrowsInvalidDataException(byte dictionaryCode)
    {
        var decoder = new BlastDecoder(new MemoryStream([0x00, dictionaryCode, 0x00]));

        Assert.Throws<InvalidDataException>(() => decoder.Read(new byte[16]));
    }

    [Fact]
    public void Read_DistanceBeforeStartOfOutput_ThrowsInvalidDataException()
    {
        // Same as the upstream vector minus its first literal: the copy then reaches 2 bytes back
        // after only 1 byte of output. Bit layout: flag 0, literal 'I' (0x49), then the vector's
        // copy item and end code shifted by 9 bits fewer.
        var decoder = new BlastDecoder(new MemoryStream(BuildVectorWithoutFirstLiteral()));

        var exception = Assert.Throws<InvalidDataException>(() => ReadAll(decoder, chunkSize: 4096));
        Assert.Contains("refers 2 bytes back, but only 1 bytes", exception.Message, StringComparison.Ordinal);
    }

    private static byte[] BuildVectorWithoutFirstLiteral()
    {
        // Rebuild the bit stream of the upstream vector after its two header bytes, drop the first
        // 9 bits (flag + literal 'A') and repack it. Trailing padding bits are ones, as in the
        // original, which keeps the end code intact.
        var bits = new List<int>();
        for (var i = 2; i < UpstreamVector.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                bits.Add((UpstreamVector[i] >> bit) & 1);
            }
        }

        bits.RemoveRange(0, 9);
        while (bits.Count % 8 != 0)
        {
            bits.Add(1);
        }

        var body = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            body[i / 8] |= (byte)(bits[i] << (i % 8));
        }

        return [UpstreamVector[0], UpstreamVector[1], .. body];
    }

    private static byte[] ReadAll(BlastDecoder decoder, int chunkSize)
    {
        var output = new MemoryStream();
        var buffer = new byte[chunkSize];
        int read;
        while ((read = decoder.Read(buffer)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    // Returns at most one byte per Read call, to exercise input refills at every byte.
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0 || _position == data.Length)
            {
                return 0;
            }

            buffer[offset] = data[_position++];
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
