namespace DbSus.Dbf;

/// <summary>
/// The table header of a DBF file.
/// </summary>
public sealed class DbfHeader
{
    private const int TableHeaderLength = 32;
    private const int DescriptorLength = 32;
    private const byte FieldTerminator = 0x0D;

    private DbfHeader(
        byte version,
        DateTime? lastUpdate,
        long recordCount,
        int headerLength,
        int recordLength,
        byte languageDriverId,
        IReadOnlyList<DbfField> fields,
        int unusedRecordBytes)
    {
        Version = version;
        LastUpdate = lastUpdate;
        RecordCount = recordCount;
        HeaderLength = headerLength;
        RecordLength = recordLength;
        LanguageDriverId = languageDriverId;
        Fields = fields;
        UnusedRecordBytes = unusedRecordBytes;
    }

    /// <summary>Gets the version byte (0x03 for dBase III without memo, as used by DATASUS).</summary>
    public byte Version { get; }

    /// <summary>
    /// Gets the date of last update stored in the header, or <see langword="null"/> when the stored
    /// bytes do not form a valid date.
    /// </summary>
    public DateTime? LastUpdate { get; }

    /// <summary>Gets the number of records declared in the header, deleted ones included.</summary>
    public long RecordCount { get; }

    /// <summary>Gets the header length in bytes, as declared.</summary>
    public int HeaderLength { get; }

    /// <summary>Gets the record length in bytes, including the one-byte deletion flag.</summary>
    public int RecordLength { get; }

    /// <summary>Gets the language driver ID (LDID) byte, which identifies the code page; 0 when unset.</summary>
    public byte LanguageDriverId { get; }

    /// <summary>Gets all fields declared in the file, in file order.</summary>
    public IReadOnlyList<DbfField> Fields { get; }

    /// <summary>
    /// Gets the number of bytes at the end of each record that no field covers. It is 0 in every
    /// DATASUS file examined; a positive value is tolerated and the bytes are ignored.
    /// </summary>
    public int UnusedRecordBytes { get; }

    internal static DbfHeader Parse(byte[] header)
    {
        var headerLength = header[8] | (header[9] << 8);
        var recordLength = header[10] | (header[11] << 8);

        // Descriptors run until the 0x0D terminator. Some files (e.g. CNES STRR2301) store 0x00
        // there instead, so the header length also bounds them, and an empty name ends them too.
        var fields = new List<DbfField>();
        var offset = 1;
        for (var position = TableHeaderLength;
             position + DescriptorLength <= headerLength && header[position] != FieldTerminator && header[position] != 0;
             position += DescriptorLength)
        {
            var length = header[position + 16];
            var decimals = header[position + 17];
            fields.Add(new DbfField(ReadName(header, position), (char)header[position + 11], length, decimals, fields.Count, offset));
            offset += length;
        }

        if (offset > recordLength)
        {
            throw new InvalidDataException(
                $"Invalid DBF header: the {fields.Count} fields need {offset} bytes per record, but the record length is {recordLength}.");
        }

        return new DbfHeader(
            header[0],
            ReadDate(header[1], header[2], header[3]),
            header[4] | ((long)header[5] << 8) | ((long)header[6] << 16) | ((long)header[7] << 24),
            headerLength,
            recordLength,
            header[29],
            fields,
            recordLength - offset);
    }

    private static string ReadName(byte[] header, int position)
    {
        var end = position;
        while (end < position + 11 && header[end] != 0)
        {
            end++;
        }

        var chars = new char[end - position];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)header[position + i]; // field names are ASCII; Latin-1 keeps any stray byte visible
        }

        return new string(chars).Trim();
    }

    private static DateTime? ReadDate(byte year, byte month, byte day)
    {
        var fullYear = 1900 + year;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(fullYear, month))
        {
            return null;
        }

        return new DateTime(fullYear, month, day, 0, 0, 0, DateTimeKind.Unspecified);
    }
}
