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
}
