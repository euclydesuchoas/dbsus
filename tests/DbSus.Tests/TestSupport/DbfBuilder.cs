using System.Text;

namespace DbSus.Tests.TestSupport;

/// <summary>
/// Builds DBF files in memory for tests, including the irregular shapes seen in DATASUS files
/// (missing field terminator, spare record bytes, no end-of-file marker).
/// </summary>
internal sealed class DbfBuilder
{
    private readonly List<(string Name, char Type, byte Length, byte Decimals)> _fields = [];
    private readonly List<(bool Deleted, object[] Values)> _records = [];

    /// <summary>Encoding for string values; bytes given as <c>byte[]</c> are written as is.</summary>
    public Encoding Encoding { get; set; } = Encoding.GetEncoding("ISO-8859-1");

    public byte LanguageDriverId { get; set; } = 0x03;

    /// <summary>Byte written after the descriptors; 0x0D normally, 0x00 like CNES STRR2301.</summary>
    public byte Terminator { get; set; } = 0x0D;

    public int ExtraHeaderBytes { get; set; }

    public int ExtraRecordBytes { get; set; }

    public bool EndOfFileMarker { get; set; } = true;

    public long? DeclaredRecordCount { get; set; }

    public byte[] LastUpdate { get; set; } = [126, 9, 30]; // 2026-09-30

    public int? DeclaredRecordLength { get; set; }

    public DbfBuilder Field(string name, char type, int length, int decimals = 0)
    {
        _fields.Add((name, type, (byte)length, (byte)decimals));
        return this;
    }

    /// <summary>Adds a record; each value is a string (padded right with spaces) or raw bytes.</summary>
    public DbfBuilder Record(params object[] values)
    {
        _records.Add((false, values));
        return this;
    }

    public DbfBuilder DeletedRecord(params object[] values)
    {
        _records.Add((true, values));
        return this;
    }

    public byte[] Build()
    {
        var headerLength = 32 + (32 * _fields.Count) + 1 + ExtraHeaderBytes;
        var recordLength = DeclaredRecordLength ?? (1 + _fields.Sum(f => f.Length) + ExtraRecordBytes);
        var output = new MemoryStream();

        var header = new byte[32];
        header[0] = 0x03;
        LastUpdate.CopyTo(header, 1);
        BitConverter.GetBytes((uint)(DeclaredRecordCount ?? _records.Count)).CopyTo(header, 4);
        BitConverter.GetBytes((ushort)headerLength).CopyTo(header, 8);
        BitConverter.GetBytes((ushort)recordLength).CopyTo(header, 10);
        header[29] = LanguageDriverId;
        output.Write(header, 0, header.Length);

        foreach (var (name, type, length, decimals) in _fields)
        {
            var descriptor = new byte[32];
            Encoding.ASCII.GetBytes(name).CopyTo(descriptor, 0);
            descriptor[11] = (byte)type;
            descriptor[16] = length;
            descriptor[17] = decimals;
            output.Write(descriptor, 0, descriptor.Length);
        }

        output.WriteByte(Terminator);
        output.Write(new byte[ExtraHeaderBytes], 0, ExtraHeaderBytes);

        foreach (var (deleted, values) in _records)
        {
            output.WriteByte(deleted ? (byte)'*' : (byte)' ');
            for (var i = 0; i < _fields.Count; i++)
            {
                var bytes = values[i] as byte[] ?? Encoding.GetBytes((string)values[i]);
                var cell = Enumerable.Repeat((byte)' ', _fields[i].Length).ToArray();
                Array.Copy(bytes, cell, Math.Min(bytes.Length, cell.Length));
                output.Write(cell, 0, cell.Length);
            }

            for (var i = 0; i < ExtraRecordBytes; i++)
            {
                output.WriteByte((byte)' ');
            }
        }

        if (EndOfFileMarker)
        {
            output.WriteByte(0x1A);
        }

        return output.ToArray();
    }
}
