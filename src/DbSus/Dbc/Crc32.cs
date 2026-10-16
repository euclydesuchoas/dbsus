namespace DbSus.Dbc;

/// <summary>
/// CRC-32 with the IEEE 802.3 / zlib polynomial (reflected 0xEDB88320), the checksum DBC files
/// store for their uncompressed DBF. Implemented here to avoid a package dependency.
/// </summary>
/// <remarks>
/// Slicing-by-8: eight bytes per step, through eight 256-entry tables, where table k gives the
/// CRC contribution of a byte followed by k zero bytes. The remaining bytes go one at a time
/// through the first table.
/// </remarks>
internal struct Crc32
{
    private static readonly uint[][] Tables = CreateTables();

    private uint _state;

    public Crc32()
    {
        _state = 0xFFFFFFFFu;
    }

    public readonly uint Value => ~_state;

    public void Append(ReadOnlySpan<byte> data)
    {
        var tables = Tables;
        uint[] t0 = tables[0], t1 = tables[1], t2 = tables[2], t3 = tables[3];
        uint[] t4 = tables[4], t5 = tables[5], t6 = tables[6], t7 = tables[7];
        var state = _state;
        var i = 0;

        // Indexing instead of slicing: as fast on .NET 10 and faster on .NET Framework (measured).
        for (var end = data.Length - 7; i < end; i += 8)
        {
            state ^= (uint)(data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24));
            state = t7[state & 0xFF] ^ t6[(state >> 8) & 0xFF] ^ t5[(state >> 16) & 0xFF] ^ t4[state >> 24]
                ^ t3[data[i + 4]] ^ t2[data[i + 5]] ^ t1[data[i + 6]] ^ t0[data[i + 7]];
        }

        for (; i < data.Length; i++)
        {
            state = t0[(state ^ data[i]) & 0xFF] ^ (state >> 8);
        }

        _state = state;
    }

    private static uint[][] CreateTables()
    {
        var tables = new uint[8][];
        for (var k = 0; k < tables.Length; k++)
        {
            tables[k] = new uint[256];
        }

        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            tables[0][n] = c;
        }

        for (var n = 0; n < 256; n++)
        {
            for (var k = 1; k < tables.Length; k++)
            {
                var previous = tables[k - 1][n];
                tables[k][n] = tables[0][previous & 0xFF] ^ (previous >> 8);
            }
        }

        return tables;
    }
}
