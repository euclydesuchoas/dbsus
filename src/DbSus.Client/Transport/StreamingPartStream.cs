namespace DbSus.Client.Transport;

/// <summary>
/// One part of a reader from <c>OpenStreamingReaderAsync</c>: the bytes read ahead at the opening
/// (the header and its checksum; none for the first part) come from memory, and the first read
/// beyond them starts the body's transfer on the session's connection, once the part before it is
/// done with it: from byte 0, dropping the bytes read ahead, so that REST is sent only to resume
/// after a failure. Disposing it, from any thread, makes a read waiting for it end with
/// <see cref="ObjectDisposedException"/>, and stops the transfer unless every byte has arrived (the
/// transfer then ends on its own, and the connection stays good for the next part). Once every
/// part is disposed, the session stops every transfer still under way, even one with every byte.
/// </summary>
internal sealed class StreamingPartStream : Stream
{
    private readonly StreamingSession _session;
    private readonly RetryPolicy _retry;
    private readonly RemotePath _remote;
    private readonly RemoteFileInfo _expected;
    private readonly byte[] _ahead;
    private readonly long _restartFromStartUpTo;
    private readonly CancellationToken _cancellationToken;
    private readonly object _gate = new();
    private RemoteReadAheadStream? _body;
    private int _position;
    private bool _disposed;

    public StreamingPartStream(StreamingSession session, RetryPolicy retry, RemotePath remote, RemoteFileInfo expected, byte[] ahead, long restartFromStartUpTo, CancellationToken cancellationToken)
    {
        _session = session;
        _retry = retry;
        _remote = remote;
        _expected = expected;
        _ahead = ahead;
        _restartFromStartUpTo = restartFromStartUpTo;
        _cancellationToken = cancellationToken;
        session.AddPart();
    }

    public override bool CanRead => !Volatile.Read(ref _disposed);

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
        Guard.NotNull(buffer, nameof(buffer));
        if (_position < _ahead.Length)
        {
            Guard.NotDisposed(Volatile.Read(ref _disposed), this);
            var taken = Math.Min(count, _ahead.Length - _position);
            Buffer.BlockCopy(_ahead, _position, buffer, offset, taken);
            _position += taken;
            return taken;
        }

        RemoteReadAheadStream body;
        lock (_gate)
        {
            Guard.NotDisposed(_disposed, this);
            if (_body is null)
            {
                _body = _session.StartPart(after => RemoteReadAheadStream.StartOnLease(_session.Lease, _retry, _remote, _expected, _ahead.Length, _restartFromStartUpTo, after, _session.Failed, _cancellationToken));
            }

            body = _body;
        }

        return body.Read(buffer, offset, count);
    }

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
            RemoteReadAheadStream? body;
            lock (_gate)
            {
                if (_disposed)
                {
                    base.Dispose(disposing);
                    return;
                }

                _disposed = true;
                body = _body;
            }

            // A read waiting for the pump ends at once. The session is let go once the pump of this
            // part (if any) is over and every part is disposed.
            body?.Dispose();
            _session.PartDisposed();
        }

        base.Dispose(disposing);
    }
}
