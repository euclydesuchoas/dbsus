namespace DbSus.Tests.TestSupport;

/// <summary>
/// Bit-by-bit CRC-32 (IEEE, reflected polynomial 0xEDB88320), written independently of the
/// table-driven implementation in the library so tests do not check the code against itself.
/// </summary>
internal static class ReferenceCrc32
{
    public static uint Compute(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                var mask = 0u - (crc & 1);
                crc = (crc >> 1) ^ (0xEDB88320u & mask);
            }
        }

        return ~crc;
    }

    /// <summary>
    /// Byte-at-a-time CRC-32 through one 256-entry table: the library's implementation before
    /// slicing-by-8, kept here as a second reference.
    /// </summary>
    public static uint ComputeByteWise(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = ByteTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static readonly uint[] ByteTable = CreateByteTable();

    private static uint[] CreateByteTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < table.Length; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
