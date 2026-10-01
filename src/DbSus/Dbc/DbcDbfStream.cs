using DbSus.Dbc.Implode;

namespace DbSus.Dbc;

/// <summary>
/// Read-only, forward-only stream that turns a DBC into the DBF it contains, decompressing on
/// demand. The DBC stream is read sequentially and never seeked.
/// </summary>
/// <remarks>
/// DBC layout (see docs/decisions.md): the DBF header verbatim (its length is the UInt16 at offset
/// 8), then the CRC-32 of the whole DBF as a little-endian UInt32, then a PKWare DCL implode stream
/// holding the rest of the DBF. Output bytes are passed through exactly as stored, including the
/// optional 0x1A end-of-file marker.
/// </remarks>
internal sealed class DbcDbfStream : Stream
{
    private const int MinimumHeaderLength = 33; // 32-byte table header + field terminator
    private const int HeaderLengthOffset = 8;

    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly bool _verifyChecksum;
    private readonly int _partIndex;
    private readonly string? _partName;
    private readonly Action<int, string?>? _checksumMismatch;
    private readonly byte[] _header;
    private readonly uint _expectedChecksum;
    private readonly BlastDecoder _decoder;
    private Crc32 _checksum = new();
    private int _headerPosition;
    private bool _checksumVerified;
    private bool _disposed;

    public DbcDbfStream(Stream source, bool leaveOpen, DbcReadOptions options, int partIndex, string? partName)
    {
        _source = source;
        _leaveOpen = leaveOpen;
        _verifyChecksum = options.VerifyChecksum;
        _checksumMismatch = options.ChecksumMismatch;
        _partIndex = partIndex;
        _partName = partName;

        var prefix = new byte[HeaderLengthOffset + 2];
        ReadExactly(source, prefix, 0, prefix.Length, "DBF header");
        var headerLength = prefix[HeaderLengthOffset] | (prefix[HeaderLengthOffset + 1] << 8);
        if (headerLength < MinimumHeaderLength)
        {
            throw new InvalidDataException(
                $"Invalid DBC: the DBF header length is {headerLength}, below the minimum of {MinimumHeaderLength}.");
        }

        _header = new byte[headerLength];
        Buffer.BlockCopy(prefix, 0, _header, 0, prefix.Length);
        ReadExactly(source, _header, prefix.Length, headerLength - prefix.Length, "DBF header");

        var checksum = new byte[4];
        ReadExactly(source, checksum, 0, checksum.Length, "CRC-32");
        _expectedChecksum = (uint)(checksum[0] | (checksum[1] << 8) | (checksum[2] << 16) | (checksum[3] << 24));

        _decoder = new BlastDecoder(source);
    }

    public override bool CanRead => !_disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("The length of the DBF is not known before decompression.");

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Guard.NotNull(buffer, nameof(buffer));
        if (offset < 0 || count < 0 || count > buffer.Length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Offset and count do not describe a valid range of the buffer.");
        }

        return ReadCore(buffer.AsSpan(offset, count));
    }

#if NET
    public override int Read(Span<byte> buffer) => ReadCore(buffer);
#endif

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _source.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private int ReadCore(Span<byte> destination)
    {
        Guard.NotDisposed(_disposed, this);

        if (destination.IsEmpty)
        {
            return 0;
        }

        if (_headerPosition < _header.Length)
        {
            var count = Math.Min(destination.Length, _header.Length - _headerPosition);
            var chunk = _header.AsSpan(_headerPosition, count);
            chunk.CopyTo(destination);
            _checksum.Append(chunk);
            _headerPosition += count;
            return count;
        }

        var read = _decoder.Read(destination);
        if (read > 0)
        {
            _checksum.Append(destination.Slice(0, read));
            return read;
        }

        VerifyChecksumOnce();
        return 0;
    }

    private void VerifyChecksumOnce()
    {
        if (!_verifyChecksum || _checksumVerified)
        {
            return;
        }

        _checksumVerified = true;
        var actual = _checksum.Value;
        if (actual == _expectedChecksum)
        {
            return;
        }

        NotifyMismatch();
        throw new InvalidDataException(
            $"DBC checksum mismatch: the file declares CRC-32 0x{_expectedChecksum:X8} but the decompressed DBF has 0x{actual:X8}.");
    }

    // By contract the checksum exception is the one that propagates, whatever the callback does.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031", Justification = "A failing callback must not replace the checksum exception.")]
    private void NotifyMismatch()
    {
        try
        {
            _checksumMismatch?.Invoke(_partIndex, _partName);
        }
        catch (Exception)
        {
            // Deliberately ignored; see DbcReadOptions.ChecksumMismatch.
        }
    }

    private static void ReadExactly(Stream source, byte[] buffer, int offset, int count, string part)
    {
        while (count > 0)
        {
            var read = source.Read(buffer, offset, count);
            if (read <= 0)
            {
                throw new InvalidDataException($"Unexpected end of DBC data while reading the {part}.");
            }

            offset += read;
            count -= read;
        }
    }
}
