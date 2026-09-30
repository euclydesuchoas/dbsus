namespace DbSus.Tests.TestSupport;

/// <summary>
/// Forward-only stream that returns at most one byte per Read call. Seeking, Length and Position
/// all throw, so any code path that tries to seek fails the test.
/// </summary>
internal sealed class TrickleStream(byte[] data) : Stream
{
    private int _position;

    public bool IsDisposed { get; private set; }

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

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
