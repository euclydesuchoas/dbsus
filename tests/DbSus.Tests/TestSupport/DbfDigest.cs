using System.Security.Cryptography;

namespace DbSus.Tests.TestSupport;

/// <summary>
/// Reads a DBF stream to the end in chunks of a given size and records what the sample tests
/// check: size, SHA-256, the table header fields and the last byte.
/// </summary>
internal sealed class DbfDigest
{
    private DbfDigest(long size, string sha256, uint records, ushort headerLength, ushort recordLength, byte lastByte)
    {
        Size = size;
        Sha256 = sha256;
        Records = records;
        HeaderLength = headerLength;
        RecordLength = recordLength;
        LastByte = lastByte;
    }

    public long Size { get; }

    public string Sha256 { get; }

    public uint Records { get; }

    public ushort HeaderLength { get; }

    public ushort RecordLength { get; }

    public byte LastByte { get; }

    /// <summary>Header + records, plus an optional 0x1A end-of-file marker.</summary>
    public bool SizeMatchesHeader
    {
        get
        {
            var tableSize = HeaderLength + ((long)Records * RecordLength);
            return Size == tableSize || (Size == tableSize + 1 && LastByte == 0x1A);
        }
    }

    public static DbfDigest Read(Stream dbf, int chunkSize)
    {
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[chunkSize];
        var header = new byte[12];
        long size = 0;
        byte lastByte = 0;
        int read;
        while ((read = dbf.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read && size + i < header.Length; i++)
            {
                header[size + i] = buffer[i];
            }

            sha256.AppendData(buffer, 0, read);
            size += read;
            lastByte = buffer[read - 1];
        }

        return new DbfDigest(
            size,
            ExpectedSamples.Sha256Hex(sha256.GetHashAndReset()),
            BitConverter.ToUInt32(header, 4),
            BitConverter.ToUInt16(header, 8),
            BitConverter.ToUInt16(header, 10),
            lastByte);
    }
}
