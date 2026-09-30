namespace DbSus.Dbf;

/// <summary>
/// A field (column) of a DBF table, as declared in the file header.
/// </summary>
public sealed class DbfField
{
    internal DbfField(string name, char typeCode, int length, int decimalCount, int index, int offset)
    {
        Name = name;
        TypeCode = typeCode;
        Length = length;
        DecimalCount = decimalCount;
        Index = index;
        Offset = offset;
        Kind = KindOf(typeCode, length, decimalCount);
        FieldType = TypeOf(Kind);
    }

    /// <summary>Gets the field name, trimmed.</summary>
    public string Name { get; }

    /// <summary>
    /// Gets the dBase type code as stored: <c>C</c> (character), <c>N</c> (numeric), <c>F</c>
    /// (float), <c>D</c> (date), <c>L</c> (logical), or another code for unsupported types.
    /// </summary>
    public char TypeCode { get; }

    /// <summary>Gets the field width in bytes.</summary>
    public int Length { get; }

    /// <summary>Gets the number of decimal places declared for numeric fields.</summary>
    public int DecimalCount { get; }

    /// <summary>Gets the zero-based position of the field in the file, before any projection.</summary>
    public int Index { get; }

    /// <summary>
    /// Gets the .NET type of the field's values: <see cref="string"/> for <c>C</c>;
    /// <see cref="int"/>, <see cref="long"/> or <see cref="decimal"/> for <c>N</c> depending on width
    /// and decimals; <see cref="decimal"/> for <c>F</c>; <see cref="DateTime"/> for <c>D</c>;
    /// <see cref="bool"/> for <c>L</c>; and <c>byte[]</c> (the raw stored bytes) for
    /// unsupported types.
    /// </summary>
    public Type FieldType { get; }

    /// <summary>
    /// Gets a value indicating whether DbSus interprets this field's type. Unsupported fields (for
    /// example <c>M</c>, memo, whose data lives in a separate file) are exposed only as raw bytes.
    /// </summary>
    public bool IsSupported => Kind != DbfValueKind.Raw;

    // Byte offset of the field inside the record, counting the deletion flag at offset 0.
    internal int Offset { get; }

    internal DbfValueKind Kind { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} {TypeCode}({Length},{DecimalCount})";

    private static DbfValueKind KindOf(char typeCode, int length, int decimalCount) => typeCode switch
    {
        'C' => DbfValueKind.String,
        'N' when decimalCount == 0 && length <= 9 => DbfValueKind.Int32,
        'N' when decimalCount == 0 && length <= 18 => DbfValueKind.Int64,
        'N' or 'F' => DbfValueKind.Decimal,
        'D' => DbfValueKind.Date,
        'L' => DbfValueKind.Boolean,
        _ => DbfValueKind.Raw,
    };

    private static Type TypeOf(DbfValueKind kind) => kind switch
    {
        DbfValueKind.String => typeof(string),
        DbfValueKind.Int32 => typeof(int),
        DbfValueKind.Int64 => typeof(long),
        DbfValueKind.Decimal => typeof(decimal),
        DbfValueKind.Date => typeof(DateTime),
        DbfValueKind.Boolean => typeof(bool),
        _ => typeof(byte[]),
    };
}

internal enum DbfValueKind
{
    String,
    Int32,
    Int64,
    Decimal,
    Date,
    Boolean,
    Raw,
}
