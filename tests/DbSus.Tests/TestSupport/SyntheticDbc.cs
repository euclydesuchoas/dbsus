using System.Text;

namespace DbSus.Tests.TestSupport;

/// <summary>
/// Builds small DBF and DBC files in memory, so tests cover the DBC wrapper without real
/// microdata. The DBC body uses the simplest valid implode encoding: uncoded literals only.
/// </summary>
internal static class SyntheticDbc
{
    /// <summary>
    /// A three-field, three-record DBF in the shape DATASUS uses: dBase III (0x03), character and
    /// numeric fields, blank deletion flags, optionally followed by the 0x1A end-of-file marker.
    /// </summary>
    public static byte[] BuildDbf(bool withEndOfFileMarker)
    {
        (string Name, char Type, byte Length, byte Decimals)[] fields =
        [
            ("CODE", 'C', 6, 0),
            ("NAME", 'C', 10, 0),
            ("VALUE", 'N', 8, 2),
        ];
        string[][] records =
        [
            ["000001", "ALPHA", "   12.50"],
            ["000002", "BETA", "    0.00"],
            ["000003", "GAMMA", " 1234.75"],
        ];

        var headerLength = 32 + (32 * fields.Length) + 1;
        var recordLength = 1 + fields.Sum(f => f.Length);
        var output = new MemoryStream();

        var header = new byte[32];
        header[0] = 0x03;
        header[1] = 126; // 2026
        header[2] = 9;
        header[3] = 30;
        BitConverter.GetBytes((uint)records.Length).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)headerLength).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)recordLength).CopyTo(header, 10);
        header[29] = 0x02;
        output.Write(header, 0, header.Length);

        foreach (var (name, type, length, decimals) in fields)
        {
            var descriptor = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(descriptor, 0);
            descriptor[11] = (byte)type;
            descriptor[16] = length;
            descriptor[17] = decimals;
            output.Write(descriptor, 0, descriptor.Length);
        }

        output.WriteByte(0x0D);

        foreach (var record in records)
        {
            output.WriteByte((byte)' ');
            for (var i = 0; i < fields.Length; i++)
            {
                var value = Encoding.ASCII.GetBytes(record[i].PadRight(fields[i].Length));
                output.Write(value, 0, value.Length);
            }
        }

        if (withEndOfFileMarker)
        {
            output.WriteByte(0x1A);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Wraps a DBF as a DBC: header verbatim, CRC-32 (little-endian) of the whole DBF, then the
    /// rest of the DBF imploded. <paramref name="checksum"/> overrides the stored CRC-32.
    /// </summary>
    public static byte[] BuildDbc(byte[] dbf, uint? checksum = null)
    {
        var headerLength = dbf[8] | (dbf[9] << 8);
        var crc = checksum ?? ReferenceCrc32.Compute(dbf);

        var output = new MemoryStream();
        output.Write(dbf, 0, headerLength);
        output.Write(BitConverter.GetBytes(crc), 0, 4);
        var body = ImplodeLiteralsOnly(dbf.AsSpan(headerLength).ToArray());
        output.Write(body, 0, body.Length);
        return output.ToArray();
    }

    /// <summary>
    /// Encodes <paramref name="data"/> as a PKWare DCL implode stream made only of uncoded literals.
    /// </summary>
    /// <remarks>
    /// Header: 0 (uncoded literals), 6 (4 KiB dictionary). Each literal is a 0 flag bit followed
    /// by the byte's 8 bits, least significant first. The end code is a 1 flag bit, then length
    /// symbol 15 (canonical code 1111111 of the 7-bit lengths, stored inverted as seven 0 bits),
    /// then 8 extra bits of value 255: 264 + 255 = 519, the end-of-stream length.
    /// </remarks>
    public static byte[] ImplodeLiteralsOnly(byte[] data)
    {
        var writer = new BitWriter();
        writer.WriteBits(0x00, 8);
        writer.WriteBits(0x06, 8);
        foreach (var value in data)
        {
            writer.WriteBits(0, 1);
            writer.WriteBits(value, 8);
        }

        writer.WriteBits(1, 1);
        writer.WriteBits(0, 7);
        writer.WriteBits(0xFF, 8);
        return writer.ToArray();
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _buffer;
        private int _count;

        // Writes the low `count` bits of `value`, least significant bit first.
        public void WriteBits(int value, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _buffer |= ((value >> i) & 1) << _count;
                _count++;
                if (_count == 8)
                {
                    _bytes.Add((byte)_buffer);
                    _buffer = 0;
                    _count = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            var result = new List<byte>(_bytes);
            if (_count > 0)
            {
                result.Add((byte)_buffer);
            }

            return [.. result];
        }
    }
}
