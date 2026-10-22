namespace DbSus.Tests.TestSupport;

/// <summary>
/// Wraps a stream and returns fewer bytes than asked: at most <c>maxChunk</c> per Read, a random
/// number from 1 up to it for a fixed seed, as a network stream returns what has arrived. Seeking
/// throws, so any code path that tries to seek fails the test.
/// </summary>
internal sealed class ShortReadStream(Stream inner, int maxChunk, int seed = 0) : Stream
{
    private readonly Random _random = new(seed);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        count == 0 ? 0 : inner.Read(buffer, offset, Math.Min(count, _random.Next(1, maxChunk + 1)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
