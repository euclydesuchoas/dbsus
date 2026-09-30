namespace DbSus.Dbc;

/// <summary>
/// CRC-32 with the IEEE 802.3 / zlib polynomial (reflected 0xEDB88320), the checksum DBC files
/// store for their uncompressed DBF. Implemented here to avoid a package dependency.
/// </summary>
internal struct Crc32
{
    private static readonly uint[] Table = CreateTable();

    private uint _state;

    public Crc32()
    {
        _state = 0xFFFFFFFFu;
    }

    public readonly uint Value => ~_state;

    public void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        foreach (var value in data)
        {
            state = Table[(state ^ value) & 0xFF] ^ (state >> 8);
        }

        _state = state;
    }

    private static uint[] CreateTable()
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
