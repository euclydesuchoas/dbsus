using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace DbSus.Dbf;

/// <summary>
/// Forward-only reader for a DBF table. It is a <see cref="DbDataReader"/>, so it works with
/// <c>DataTable.Load</c>, <c>SqlBulkCopy</c>, Dapper and other ADO.NET consumers, and it adds
/// getters by field name and access to the raw stored bytes.
/// </summary>
/// <remarks>
/// <para>
/// Records are read sequentially from the underlying stream, which is never seeked. Values are
/// parsed only when requested. For speed in loops over many records, look up ordinals once with
/// <see cref="GetOrdinal(string)"/> before the loop instead of using the by-name overloads on
/// every record.
/// </para>
/// <para>
/// A table split into parts (SIA-PA's <c>a</c>, <c>b</c>, <c>c</c>, ... files) can be read as one:
/// every part's header is read and checked when the reader opens, before any record, and the
/// parts must have identical fields (names, types, widths, decimals), record length and encoding.
/// <see cref="RecordNumber"/> counts across parts; <see cref="PartIndex"/> tells the current part.
/// Messages name the part, never the values.
/// </para>
/// <para>
/// A reader is not thread-safe. The asynchronous methods inherited from
/// <see cref="DbDataReader"/> (such as <c>ReadAsync</c>) currently complete synchronously.
/// </para>
/// <para>
/// Value mapping: <c>C</c> is a <see cref="string"/> without trailing spaces (all blank is an
/// empty string, or <see cref="DBNull"/> with <see cref="DbfReadOptions.BlankStringsAsNull"/>);
/// <c>N</c> without decimals is an <see cref="int"/> up to 9 digits wide, a <see cref="long"/> up
/// to 18 and a <see cref="decimal"/> beyond; <c>N</c> with decimals and <c>F</c> are
/// <see cref="decimal"/>; <c>D</c> is a <see cref="DateTime"/> (<c>yyyyMMdd</c>); <c>L</c> is a
/// <see cref="bool"/>. Blank numbers, dates and logicals, and dates made of zeros, are
/// <see cref="DBNull"/>. Content that does not parse throws <see cref="FormatException"/>; the
/// message names the field and the record but, deliberately, not the value.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1010", Justification = "The non-generic IEnumerable comes from DbDataReader; its items are records, as in every ADO.NET reader.")]
public sealed class DbfReader : DbDataReader, IDbColumnSchemaGenerator
{
    private const byte DeletedFlag = (byte)'*';
    private const byte Space = (byte)' ';

    private readonly DbfPart[] _parts;
    private readonly bool _includeDeleted;
    private readonly bool _blankStringsAsNull;
    private readonly DbfField[] _fields;
    private readonly Dictionary<string, int> _ordinals;
    private readonly byte[] _record;
    private int _partIndex;
    private long _consumedInPart;
    private long _recordNumber;
    private bool _hasRecord;
    private bool _closed;

    private DbfReader(DbfPart[] parts, DbfReadOptions options)
    {
        _parts = parts;
        _includeDeleted = options.IncludeDeletedRecords;
        _blankStringsAsNull = options.BlankStringsAsNull;

        var first = parts[0];
        foreach (var part in parts)
        {
            part.Header = ReadHeader(part);
        }

        Encoding = DbfEncodings.Resolve(first.Header!.LanguageDriverId, options);
        for (var i = 1; i < parts.Length; i++)
        {
            EnsureSameSchema(first, parts[i], options, Encoding);
        }

        TotalRecordCount = parts.Sum(part => part.Header!.RecordCount);
        _fields = Project(ApplyConversions([.. first.Header.Fields], options.Conversions), options.Columns);
        Fields = new ReadOnlyCollection<DbfField>(_fields);
        // Backwards, so that with duplicate names the first field wins.
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = _fields.Length - 1; i >= 0; i--)
        {
            _ordinals[_fields[i].Name] = i;
        }

        _record = new byte[first.Header.RecordLength];
    }

    /// <summary>
    /// Gets the header of the current part (the first part before the first <see cref="Read"/>).
    /// For a single file, simply its header.
    /// </summary>
    public DbfHeader Header => _parts[_partIndex].Header!;

    /// <summary>Gets the number of parts this reader reads, 1 for a single file.</summary>
    public int PartCount => _parts.Length;

    /// <summary>Gets the zero-based index of the part the current record comes from.</summary>
    public int PartIndex => _partIndex;

    /// <summary>Gets the number of records declared across all parts, deleted ones included.</summary>
    public long TotalRecordCount { get; }

    /// <summary>
    /// Gets the fields this reader exposes, in ordinal order: all fields in file order, or the
    /// projection given in <see cref="DbfReadOptions.Columns"/>.
    /// </summary>
    public IReadOnlyList<DbfField> Fields { get; }

    /// <summary>Gets the encoding used for character fields.</summary>
    public Encoding Encoding { get; }

    /// <summary>
    /// Gets the 1-based physical number of the current record, counted across all parts (deleted
    /// records count), or 0 when there is no current record.
    /// </summary>
    public long RecordNumber => _hasRecord ? _recordNumber : 0;

    /// <summary>
    /// Gets a value indicating whether the current record is marked as deleted. Only possible with
    /// <see cref="DbfReadOptions.IncludeDeletedRecords"/>.
    /// </summary>
    public bool IsDeleted => CurrentRecord()[0] == DeletedFlag;

    /// <inheritdoc/>
    public override int FieldCount => _fields.Length;

    /// <summary>
    /// Gets a value indicating whether the table declares any records. It can be true even when
    /// every record is deleted and none will be returned.
    /// </summary>
    public override bool HasRows => TotalRecordCount > 0;

    /// <inheritdoc/>
    public override bool IsClosed => _closed;

    /// <summary>Gets -1: a DBF reader never modifies records.</summary>
    public override int RecordsAffected => -1;

    /// <summary>Gets 0: DBF tables have no nesting.</summary>
    public override int Depth => 0;

    /// <inheritdoc/>
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc/>
    public override object this[string name] => GetValue(GetOrdinal(name));

    /// <summary>Opens a DBF file.</summary>
    /// <param name="path">Path of the DBF file.</param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults.</param>
    /// <returns>A reader positioned before the first record. Disposing it closes the file.</returns>
    /// <exception cref="InvalidDataException">The header is invalid.</exception>
    /// <exception cref="ArgumentException">A projected column does not exist or is repeated.</exception>
    public static DbfReader Open(string path, DbfReadOptions? options = null)
    {
        Guard.NotNull(path, nameof(path));
        return Open([path], options);
    }

    /// <summary>
    /// Opens the parts of a table split into several DBF files as a single reader, in the order given.
    /// </summary>
    /// <param name="paths">Paths of the parts, in reading order (e.g. from <c>DbSusFile.FindParts</c>).</param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults. They apply to every part.</param>
    /// <returns>A reader positioned before the first record. Disposing it closes every file.</returns>
    /// <exception cref="InvalidDataException">A header is invalid, or the parts do not share the same schema.</exception>
    /// <exception cref="ArgumentException">No path is given, or a projected column does not exist or is repeated.</exception>
    public static DbfReader Open(IEnumerable<string> paths, DbfReadOptions? options = null)
    {
        var list = RequireParts(paths, nameof(paths));
        var parts = new List<DbfPart>();
        try
        {
            foreach (var path in list)
            {
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                parts.Add(new DbfPart(Path.GetFileName(path), file, [file], [file], drainAtEnd: false));
            }

            return Create(parts, options);
        }
        catch
        {
            DbfPart.DisposeCreated(parts);
            throw;
        }
    }

    /// <summary>Opens a DBF table from a stream.</summary>
    /// <param name="dbf">
    /// A readable stream positioned at the start of the DBF data. It is read sequentially and never
    /// seeked. The header is read before this method returns, so it blocks until the header arrives.
    /// </param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to hand <paramref name="dbf"/> over: it is disposed with
    /// the reader, and also if this method throws. <see langword="true"/> to keep it yours: it is
    /// never disposed.
    /// </param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="InvalidDataException">The header is invalid.</exception>
    /// <exception cref="ArgumentException">The stream is not readable, or a projected column does not exist or is repeated.</exception>
    public static DbfReader Open(Stream dbf, DbfReadOptions? options = null, bool leaveOpen = false)
    {
        Guard.NotNull(dbf, nameof(dbf));
        if (!dbf.CanRead)
        {
            if (!leaveOpen)
            {
                dbf.Dispose();
            }

            throw new ArgumentException("The DBF stream must be readable.", nameof(dbf));
        }

        return Open([dbf], options, leaveOpen);
    }

    /// <summary>
    /// Opens the parts of a table split into several DBF streams as a single reader, in the order given.
    /// </summary>
    /// <param name="parts">
    /// Readable streams positioned at the start of each part; never seeked. Every header is read
    /// before this method returns.
    /// </param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults. They apply to every part.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to hand the streams over: they are disposed with the
    /// reader (each one as soon as its part has been read), and also if this method throws.
    /// <see langword="true"/> to keep them yours: they are never disposed.
    /// </param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="InvalidDataException">A header is invalid, or the parts do not share the same schema.</exception>
    /// <exception cref="ArgumentException">No stream is given, a stream is not readable, or a projected column does not exist or is repeated.</exception>
    public static DbfReader Open(IEnumerable<Stream> parts, DbfReadOptions? options = null, bool leaveOpen = false)
    {
        Guard.NotNull(parts, nameof(parts));
        var given = parts.ToList();
        try
        {
            var list = RequireParts(given, nameof(parts));
            var wrapped = new List<DbfPart>();
            for (var i = 0; i < list.Count; i++)
            {
                if (!list[i].CanRead)
                {
                    throw new ArgumentException("Every DBF stream must be readable.", nameof(parts));
                }

                var name = list.Count == 1 ? null : $"part {i + 1}";
                wrapped.Add(new DbfPart(name, list[i], leaveOpen ? [] : [list[i]], [], drainAtEnd: false));
            }

            return Create(wrapped, options);
        }
        catch when (!leaveOpen)
        {
            DisposeStreams(given);
            throw;
        }
    }

    /// <summary>
    /// Creates a reader over prepared parts. On failure, disposes only what DbSus created for them.
    /// </summary>
    internal static DbfReader Create(IReadOnlyList<DbfPart> parts, DbfReadOptions? options)
    {
        try
        {
            return new DbfReader([.. parts], options ?? new DbfReadOptions());
        }
        catch
        {
            DbfPart.DisposeCreated(parts);
            throw;
        }
    }

    /// <summary>Disposes the streams a caller handed over (leaveOpen: false) when opening fails.</summary>
    internal static void DisposeStreams(IEnumerable<Stream?> streams)
    {
        foreach (var stream in streams)
        {
            stream?.Dispose();
        }
    }

    internal static List<T> RequireParts<T>(IEnumerable<T> parts, string paramName)
        where T : class
    {
        Guard.NotNull(parts, paramName);
        var list = parts.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one part is required.", paramName);
        }

        if (list.Any(part => part is null))
        {
            throw new ArgumentException("Parts cannot be null.", paramName);
        }

        return list;
    }

    /// <summary>
    /// Advances to the next record, skipping deleted ones unless configured otherwise, and moving on
    /// to the next part when one ends.
    /// </summary>
    /// <returns><see langword="true"/> if there is a current record; <see langword="false"/> at the end.</returns>
    /// <exception cref="InvalidDataException">
    /// The data ends before the declared number of records, or (for DBC data) a part's checksum does
    /// not match once that part has been read to its end.
    /// </exception>
    public override bool Read()
    {
        Guard.NotDisposed(_closed, this);
        while (true)
        {
            var part = _parts[_partIndex];
            if (_consumedInPart < part.Header!.RecordCount)
            {
                ReadRecord(part);
                _consumedInPart++;
                _recordNumber++;
                if (_includeDeleted || _record[0] != DeletedFlag)
                {
                    _hasRecord = true;
                    return true;
                }

                continue;
            }

            _hasRecord = false;
            FinishPart(part);
            if (_partIndex + 1 == _parts.Length)
            {
                return false;
            }

            _partIndex++;
            _consumedInPart = 0;
        }
    }

    /// <summary>Returns <see langword="false"/>: a DBF file holds a single table.</summary>
    public override bool NextResult() => false;

    /// <inheritdoc/>
    public override string GetName(int ordinal) => Field(ordinal).Name;

    /// <summary>Returns the dBase type code of a field, e.g. <c>"N"</c>.</summary>
    public override string GetDataTypeName(int ordinal) => Field(ordinal).TypeCode.ToString();

    /// <inheritdoc/>
    public override Type GetFieldType(int ordinal) => Field(ordinal).FieldType;

    /// <summary>Returns the ordinal of a field by name, ignoring case.</summary>
    /// <exception cref="IndexOutOfRangeException">No exposed field has that name.</exception>
    [SuppressMessage("Usage", "CA2201", Justification = "IndexOutOfRangeException is the documented DbDataReader contract.")]
    public override int GetOrdinal(string name)
    {
        if (TryGetOrdinal(name, out var ordinal))
        {
            return ordinal;
        }

        throw new IndexOutOfRangeException($"The DBF reader has no field named '{name}'.");
    }

    /// <summary>Looks up the ordinal of a field by name, ignoring case, without throwing.</summary>
    /// <returns><see langword="true"/> if an exposed field has that name.</returns>
    public bool TryGetOrdinal(string name, out int ordinal)
    {
        Guard.NotNull(name, nameof(name));
        return _ordinals.TryGetValue(name, out ordinal);
    }

    /// <summary>
    /// Returns the bytes stored for a field in the current record, exactly as in the file.
    /// </summary>
    /// <remarks>The span is valid only until the next call to <see cref="Read"/>.</remarks>
    public ReadOnlySpan<byte> GetRawBytes(int ordinal)
    {
        var field = Field(ordinal);
        return CurrentRecord().AsSpan(field.Offset, field.Length);
    }

    /// <summary>
    /// Returns the bytes stored for a field in the current record, exactly as in the file.
    /// </summary>
    /// <remarks>The span is valid only until the next call to <see cref="Read"/>.</remarks>
    public ReadOnlySpan<byte> GetRawBytes(string name) => GetRawBytes(GetOrdinal(name));

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal)
    {
        var field = Field(ordinal);
        var bytes = CurrentRecord().AsSpan(field.Offset, field.Length);
        switch (field.Kind)
        {
            case DbfValueKind.String:
                return _blankStringsAsNull && DbfValueParser.IsBlank(bytes);
            case DbfValueKind.Int32 or DbfValueKind.Int64 or DbfValueKind.Decimal:
                return DbfValueParser.IsBlank(bytes);
            case DbfValueKind.Date:
                return DbfValueParser.TryParseDate(DbfValueParser.Trim(bytes), field.DayFirst, out var date) && date is null;
            case DbfValueKind.Boolean:
                return DbfValueParser.TryParseBoolean(DbfValueParser.Trim(bytes), out var flag) && flag is null;
            default:
                return false;
        }
    }

    /// <summary>Returns whether a field of the current record is null, by field name.</summary>
    public bool IsDBNull(string name) => IsDBNull(GetOrdinal(name));

    /// <summary>Returns a field value boxed as its <see cref="GetFieldType(int)"/> type, or <see cref="DBNull"/>.</summary>
    /// <exception cref="FormatException">
    /// The stored content does not parse as the field's type, including an integer field whose
    /// value has a fraction or does not fit its type (<see cref="int"/> or <see cref="long"/>).
    /// </exception>
    public override object GetValue(int ordinal)
    {
        var field = Field(ordinal);
        switch (field.Kind)
        {
            case DbfValueKind.String:
                return ReadString(field) ?? (object)DBNull.Value;
            case DbfValueKind.Int32:
                return ReadInt32(field) is { } int32 ? (object)int32 : DBNull.Value;
            case DbfValueKind.Int64:
                return ReadInt64(field) is { } int64 ? (object)int64 : DBNull.Value;
            case DbfValueKind.Decimal:
                return ReadDecimal(field) is { } number ? (object)number : DBNull.Value;
            case DbfValueKind.Date:
                return ReadDate(field) is { } date ? (object)date : DBNull.Value;
            case DbfValueKind.Boolean:
                return ReadBoolean(field) is { } flag ? (object)flag : DBNull.Value;
            default:
                return GetRawBytes(ordinal).ToArray();
        }
    }

    /// <inheritdoc/>
    public override int GetValues(object[] values)
    {
        Guard.NotNull(values, nameof(values));
        var count = Math.Min(values.Length, _fields.Length);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    /// <summary>Returns a character field as a string.</summary>
    /// <exception cref="InvalidCastException">The field is not a character field, or the value is null.</exception>
    public override string GetString(int ordinal)
    {
        var field = Field(ordinal);
        RequireKind(field, DbfValueKind.String);
        return ReadString(field) ?? throw NullValue(field);
    }

    /// <summary>Returns a character field as a string, by field name.</summary>
    public string GetString(string name) => GetString(GetOrdinal(name));

    /// <summary>Returns a numeric field as a decimal.</summary>
    /// <exception cref="InvalidCastException">The field is not numeric, or the value is null.</exception>
    public override decimal GetDecimal(int ordinal) => ReadNumber(Field(ordinal));

    /// <summary>Returns a numeric field as a decimal, by field name.</summary>
    public decimal GetDecimal(string name) => GetDecimal(GetOrdinal(name));

    /// <summary>Returns a numeric field as a 32-bit integer.</summary>
    /// <remarks>
    /// On a field whose own type is <see cref="int"/> (a narrow <c>N</c> field, or a <c>C</c> field
    /// converted with <see cref="DbfFieldConversion.Int32"/>), a stored value with a fraction or out
    /// of range means the data does not match the declared type and throws
    /// <see cref="FormatException"/>. On other numeric fields the value is converted, and the
    /// failures are <see cref="InvalidCastException"/> (fraction) or <see cref="OverflowException"/>.
    /// </remarks>
    /// <exception cref="InvalidCastException">The field is not numeric, the value is null, or (on a wider field) it has a fraction.</exception>
    /// <exception cref="OverflowException">On a wider numeric field, the value does not fit.</exception>
    /// <exception cref="FormatException">On an <see cref="int"/> field, the stored content does not parse, has a fraction or does not fit.</exception>
    public override int GetInt32(int ordinal)
    {
        var field = Field(ordinal);
        if (field.Kind == DbfValueKind.Int32)
        {
            return ReadInt32(field) ?? throw NullValue(field);
        }

        return checked((int)ToWhole(field, ReadNumber(field)));
    }

    /// <summary>Returns a numeric field as a 32-bit integer, by field name.</summary>
    public int GetInt32(string name) => GetInt32(GetOrdinal(name));

    /// <summary>Returns a numeric field as a 64-bit integer.</summary>
    /// <remarks>
    /// On a field whose own type is <see cref="int"/> or <see cref="long"/> (an <c>N</c> field up to
    /// 18 digits without decimals, or a converted <c>C</c> field), a stored value with a fraction or
    /// out of range throws <see cref="FormatException"/>. On <see cref="decimal"/> fields the value
    /// is converted, and the failures are <see cref="InvalidCastException"/> (fraction) or
    /// <see cref="OverflowException"/>.
    /// </remarks>
    /// <exception cref="InvalidCastException">The field is not numeric, the value is null, or (on a decimal field) it has a fraction.</exception>
    /// <exception cref="OverflowException">On a decimal field, the value does not fit.</exception>
    /// <exception cref="FormatException">On an integer field, the stored content does not parse, has a fraction or does not fit.</exception>
    public override long GetInt64(int ordinal)
    {
        var field = Field(ordinal);
        if (field.Kind is DbfValueKind.Int32 or DbfValueKind.Int64)
        {
            return ReadInt64(field) ?? throw NullValue(field);
        }

        return checked((long)ToWhole(field, ReadNumber(field)));
    }

    /// <summary>Returns a numeric field as a 64-bit integer, by field name.</summary>
    public long GetInt64(string name) => GetInt64(GetOrdinal(name));

    /// <summary>Returns a numeric field as a 16-bit integer.</summary>
    public override short GetInt16(int ordinal)
    {
        var field = Field(ordinal);
        return checked((short)ToWhole(field, ReadNumber(field)));
    }

    /// <summary>Returns a numeric field as a byte.</summary>
    public override byte GetByte(int ordinal)
    {
        var field = Field(ordinal);
        return checked((byte)ToWhole(field, ReadNumber(field)));
    }

    /// <summary>Returns a numeric field as a double.</summary>
    public override double GetDouble(int ordinal) => (double)ReadNumber(Field(ordinal));

    /// <summary>Returns a numeric field as a double, by field name.</summary>
    public double GetDouble(string name) => GetDouble(GetOrdinal(name));

    /// <summary>Returns a numeric field as a float.</summary>
    public override float GetFloat(int ordinal) => (float)ReadNumber(Field(ordinal));

    /// <summary>Returns a date field.</summary>
    /// <exception cref="InvalidCastException">The field is not a date field, or the value is null.</exception>
    public override DateTime GetDateTime(int ordinal)
    {
        var field = Field(ordinal);
        RequireKind(field, DbfValueKind.Date);
        return ReadDate(field) ?? throw NullValue(field);
    }

    /// <summary>Returns a date field, by field name.</summary>
    public DateTime GetDateTime(string name) => GetDateTime(GetOrdinal(name));

    /// <summary>Returns a logical field.</summary>
    /// <exception cref="InvalidCastException">The field is not logical, or the value is null.</exception>
    public override bool GetBoolean(int ordinal)
    {
        var field = Field(ordinal);
        RequireKind(field, DbfValueKind.Boolean);
        return ReadBoolean(field) ?? throw NullValue(field);
    }

    /// <summary>Returns a logical field, by field name.</summary>
    public bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    /// <summary>Returns a single-character string field as a <see cref="char"/>.</summary>
    public override char GetChar(int ordinal)
    {
        var value = GetString(ordinal);
        return value.Length == 1
            ? value[0]
            : throw new InvalidCastException($"Field {Field(ordinal).Name} does not hold a single character.");
    }

    /// <summary>Copies characters of a character field's string value.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetString(ordinal);
        if (buffer is null)
        {
            return value.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, value.Length - dataOffset));
        value.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>Copies the raw stored bytes of any field (see <see cref="GetRawBytes(int)"/>).</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var raw = GetRawBytes(ordinal);
        if (buffer is null)
        {
            return raw.Length;
        }

        var count = (int)Math.Max(0, Math.Min(length, raw.Length - dataOffset));
        raw.Slice((int)dataOffset, count).CopyTo(buffer.AsSpan(bufferOffset));
        return count;
    }

    /// <summary>Not supported: DBF tables have no GUID type.</summary>
    public override Guid GetGuid(int ordinal) =>
        throw new InvalidCastException($"Field {Field(ordinal).Name} cannot be read as a Guid.");

    /// <summary>
    /// Returns a field value as <typeparamref name="T"/>. Nullable types and reference types
    /// receive <see langword="null"/> for null values; numeric types convert as the typed getters do.
    /// </summary>
    /// <remarks>
    /// On .NET 5 and later, value types such as <see cref="int"/>, <see cref="decimal"/> or
    /// <see cref="DateTime"/>, and their nullable forms, are returned without allocating. On .NET
    /// Framework (which uses the <c>netstandard2.0</c> build) the non-nullable forms do not allocate
    /// either, but the nullable forms (e.g. <c>GetFieldValue&lt;int?&gt;</c>) box on every call,
    /// about 24 bytes each (measured on .NET Framework 4.8.1, x64); there, prefer
    /// <see cref="IsDBNull(int)"/> plus a typed getter in hot loops.
    /// </remarks>
    public override T GetFieldValue<T>(int ordinal)
    {
        // The typeof(T) checks are constants per T once compiled, and on modern .NET the JIT turns
        // (T)(object)value into a plain copy for value types, so these paths do not allocate.
        if (typeof(T) == typeof(int))
        {
            return (T)(object)GetInt32(ordinal);
        }

        if (typeof(T) == typeof(long))
        {
            return (T)(object)GetInt64(ordinal);
        }

        if (typeof(T) == typeof(decimal))
        {
            return (T)(object)GetDecimal(ordinal);
        }

        if (typeof(T) == typeof(double))
        {
            return (T)(object)GetDouble(ordinal);
        }

        if (typeof(T) == typeof(float))
        {
            return (T)(object)GetFloat(ordinal);
        }

        if (typeof(T) == typeof(short))
        {
            return (T)(object)GetInt16(ordinal);
        }

        if (typeof(T) == typeof(byte))
        {
            return (T)(object)GetByte(ordinal);
        }

        if (typeof(T) == typeof(DateTime))
        {
            return (T)(object)GetDateTime(ordinal);
        }

        if (typeof(T) == typeof(bool))
        {
            return (T)(object)GetBoolean(ordinal);
        }

        if (IsDBNull(ordinal))
        {
            return default(T) is null ? default! : throw NullValue(Field(ordinal));
        }

        if (typeof(T) == typeof(int?))
        {
            return (T)(object)(int?)GetInt32(ordinal);
        }

        if (typeof(T) == typeof(long?))
        {
            return (T)(object)(long?)GetInt64(ordinal);
        }

        if (typeof(T) == typeof(decimal?))
        {
            return (T)(object)(decimal?)GetDecimal(ordinal);
        }

        if (typeof(T) == typeof(double?))
        {
            return (T)(object)(double?)GetDouble(ordinal);
        }

        if (typeof(T) == typeof(DateTime?))
        {
            return (T)(object)(DateTime?)GetDateTime(ordinal);
        }

        if (typeof(T) == typeof(bool?))
        {
            return (T)(object)(bool?)GetBoolean(ordinal);
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)GetString(ordinal);
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)GetRawBytes(ordinal).ToArray();
        }

        if (typeof(T) == typeof(float?))
        {
            return (T)(object)(float?)GetFloat(ordinal);
        }

        if (typeof(T) == typeof(short?))
        {
            return (T)(object)(short?)GetInt16(ordinal);
        }

        if (typeof(T) == typeof(byte?))
        {
            return (T)(object)(byte?)GetByte(ordinal);
        }

        return (T)GetValue(ordinal);
    }

    /// <summary>Returns a field value as <typeparamref name="T"/>, by field name.</summary>
    public T GetFieldValue<T>(string name) => GetFieldValue<T>(GetOrdinal(name));

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    /// <summary>
    /// Returns the schema of the exposed fields in the standard ADO.NET schema table layout.
    /// </summary>
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable") { Locale = System.Globalization.CultureInfo.InvariantCulture };
        table.Columns.Add(SchemaTableColumn.ColumnName, typeof(string));
        table.Columns.Add(SchemaTableColumn.ColumnOrdinal, typeof(int));
        table.Columns.Add(SchemaTableColumn.ColumnSize, typeof(int));
        table.Columns.Add(SchemaTableColumn.NumericPrecision, typeof(short));
        table.Columns.Add(SchemaTableColumn.NumericScale, typeof(short));
        table.Columns.Add(SchemaTableColumn.DataType, typeof(Type));
        table.Columns.Add(SchemaTableColumn.ProviderType, typeof(int));
        table.Columns.Add(SchemaTableColumn.AllowDBNull, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsLong, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsUnique, typeof(bool));
        table.Columns.Add(SchemaTableColumn.IsKey, typeof(bool));
        table.Columns.Add(SchemaTableColumn.BaseColumnName, typeof(string));
        table.Columns.Add(SchemaTableOptionalColumn.IsReadOnly, typeof(bool));
        table.Columns.Add("DataTypeName", typeof(string));

        for (var ordinal = 0; ordinal < _fields.Length; ordinal++)
        {
            var field = _fields[ordinal];
            var row = table.NewRow();
            row[SchemaTableColumn.ColumnName] = field.Name;
            row[SchemaTableColumn.ColumnOrdinal] = ordinal;
            row[SchemaTableColumn.ColumnSize] = field.Length;
            row[SchemaTableColumn.NumericPrecision] = IsNumeric(field) ? (object)(short)field.Length : DBNull.Value;
            row[SchemaTableColumn.NumericScale] = IsNumeric(field) ? (object)(short)field.DecimalCount : DBNull.Value;
            row[SchemaTableColumn.DataType] = field.FieldType;
            row[SchemaTableColumn.ProviderType] = (int)field.TypeCode;
            row[SchemaTableColumn.AllowDBNull] = AllowsNull(field);
            row[SchemaTableColumn.IsLong] = false;
            row[SchemaTableColumn.IsUnique] = false;
            row[SchemaTableColumn.IsKey] = false;
            row[SchemaTableColumn.BaseColumnName] = field.Name;
            row[SchemaTableOptionalColumn.IsReadOnly] = true;
            row["DataTypeName"] = field.TypeCode.ToString();
            table.Rows.Add(row);
        }

        return table;
    }

    /// <summary>Returns the schema of the exposed fields as <see cref="DbColumn"/> objects.</summary>
    public ReadOnlyCollection<DbColumn> GetColumnSchema()
    {
        var columns = new DbColumn[_fields.Length];
        for (var ordinal = 0; ordinal < _fields.Length; ordinal++)
        {
            columns[ordinal] = new DbfColumn(_fields[ordinal], ordinal, AllowsNull(_fields[ordinal]));
        }

        return new ReadOnlyCollection<DbColumn>(columns);
    }

    /// <summary>Closes the reader and, unless configured otherwise, the underlying stream.</summary>
    public override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _hasRecord = false;
        foreach (var part in _parts)
        {
            part.Release();
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Close();
        }

        base.Dispose(disposing);
    }

    private static DbfHeader ReadHeader(DbfPart part)
    {
        try
        {
            return DbfHeader.Read(part.Stream);
        }
        catch (InvalidDataException exception) when (part.Name is not null)
        {
            throw new InvalidDataException(part.Prefix + exception.Message, exception);
        }
    }

    private static void EnsureSameSchema(DbfPart first, DbfPart other, DbfReadOptions options, Encoding encoding)
    {
        var expected = first.Header!;
        var actual = other.Header!;
        string? difference = null;
        if (actual.Fields.Count != expected.Fields.Count)
        {
            difference = $"it has {actual.Fields.Count} fields and the first part has {expected.Fields.Count}";
        }
        else
        {
            for (var i = 0; i < expected.Fields.Count && difference is null; i++)
            {
                var a = expected.Fields[i];
                var b = actual.Fields[i];
                if (a.Name != b.Name || a.TypeCode != b.TypeCode || a.Length != b.Length || a.DecimalCount != b.DecimalCount)
                {
                    difference = $"field {i + 1} is {a} in the first part and {b} here";
                }
            }
        }

        if (difference is null && actual.RecordLength != expected.RecordLength)
        {
            difference = $"its record length is {actual.RecordLength} and the first part's is {expected.RecordLength}";
        }

        if (difference is null && options.Encoding is null
            && DbfEncodings.Resolve(actual.LanguageDriverId, options).CodePage != encoding.CodePage)
        {
            difference = $"its language driver ID 0x{actual.LanguageDriverId:X2} selects another code page than the first part's 0x{expected.LanguageDriverId:X2}";
        }

        if (difference is not null)
        {
            throw new InvalidDataException($"{other.Name} does not match {first.Name}: {difference}.");
        }
    }

    private static DbfField[] ApplyConversions(DbfField[] result, IDictionary<string, DbfFieldConversion> conversions)
    {
        foreach (var conversion in conversions)
        {
            var index = Array.FindIndex(result, field => string.Equals(field.Name, conversion.Key, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new ArgumentException($"Cannot convert field '{conversion.Key}': the DBF has no field with that name.", nameof(conversions));
            }

            var field = result[index];
            if (field.TypeCode != 'C')
            {
                throw new ArgumentException($"Cannot convert field {field}: only character (C) fields can be converted.", nameof(conversions));
            }

            if (conversion.Value.Kind == DbfValueKind.Date && field.Length < 8)
            {
                throw new ArgumentException($"Cannot convert field {field} to a date: it is narrower than 8 characters.", nameof(conversions));
            }

            result[index] = field.WithConversion(conversion.Value);
        }

        return result;
    }

    private static DbfField[] Project(DbfField[] fields, IReadOnlyList<string>? columns)
    {
        if (columns is null)
        {
            return fields;
        }

        if (columns.Count == 0)
        {
            throw new ArgumentException("The projection is empty; set Columns to null to expose every field.", nameof(columns));
        }

        var byName = new Dictionary<string, DbfField>(StringComparer.OrdinalIgnoreCase);
        for (var i = fields.Length - 1; i >= 0; i--)
        {
            byName[fields[i].Name] = fields[i];
        }

        var projected = new DbfField[columns.Count];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
        {
            var name = columns[i];
            if (name is null || !byName.TryGetValue(name, out var field))
            {
                throw new ArgumentException($"The DBF has no field named '{name}'.", nameof(columns));
            }

            if (!seen.Add(name))
            {
                throw new ArgumentException($"Field '{name}' appears more than once in the projection.", nameof(columns));
            }

            projected[i] = field;
        }

        return projected;
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static bool IsNumeric(DbfField field) =>
        field.Kind is DbfValueKind.Int32 or DbfValueKind.Int64 or DbfValueKind.Decimal;

    private static InvalidCastException NullValue(DbfField field) =>
        new($"Field {field.Name} is null in the current record; check IsDBNull first.");

    private static void RequireKind(DbfField field, DbfValueKind kind)
    {
        if (field.Kind != kind)
        {
            throw new InvalidCastException($"Field {field.Name} is {field.TypeCode}({field.Length},{field.DecimalCount}) and cannot be read as {kind}.");
        }
    }

    private bool AllowsNull(DbfField field) => field.Kind switch
    {
        DbfValueKind.String => _blankStringsAsNull,
        DbfValueKind.Raw => false,
        _ => true,
    };

    private void ReadRecord(DbfPart part)
    {
        var read = ReadFully(part.Stream, _record, 0, _record.Length);
        if (read != _record.Length)
        {
            _hasRecord = false;
            throw new InvalidDataException(
                $"{part.Prefix}The DBF data ends inside record {_consumedInPart + 1} of {part.Header!.RecordCount}.");
        }
    }

    // After a part's last record: read the rest of its stream (normally just the 0x1A marker) so
    // a decompressing stream reaches its end and verifies its checksum, then release the part.
    private static void FinishPart(DbfPart part)
    {
        if (part.Finished)
        {
            return;
        }

        part.Finished = true;
        if (part.DrainAtEnd)
        {
            try
            {
                var buffer = new byte[4096];
                while (part.Stream.Read(buffer, 0, buffer.Length) > 0)
                {
                }
            }
            catch (InvalidDataException exception) when (part.Name is not null
                                                         && !exception.Message.StartsWith(part.Prefix, StringComparison.Ordinal))
            {
                // Errors that do not name the part yet get its name (DBC errors already do).
                throw new InvalidDataException(part.Prefix + exception.Message, exception);
            }
        }

        part.Release();
    }

    private byte[] CurrentRecord()
    {
        Guard.NotDisposed(_closed, this);
        return _hasRecord ? _record : throw new InvalidOperationException("There is no current record; call Read first.");
    }

    [SuppressMessage("Usage", "CA2201", Justification = "IndexOutOfRangeException is the documented DbDataReader contract.")]
    private DbfField Field(int ordinal) =>
        (uint)ordinal < (uint)_fields.Length
            ? _fields[ordinal]
            : throw new IndexOutOfRangeException($"Ordinal {ordinal} is outside the {_fields.Length} fields of this reader.");

    private string? ReadString(DbfField field)
    {
        var record = CurrentRecord();
        var end = field.Offset + field.Length;
        while (end > field.Offset && record[end - 1] == Space)
        {
            end--;
        }

        if (end == field.Offset && _blankStringsAsNull)
        {
            return null;
        }

        return Encoding.GetString(record, field.Offset, end - field.Offset);
    }

    private decimal? ReadDecimal(DbfField field)
    {
        var trimmed = DbfValueParser.Trim(CurrentRecord().AsSpan(field.Offset, field.Length));
        if (trimmed.IsEmpty)
        {
            return null;
        }

        return DbfValueParser.TryParseDecimal(trimmed, out var value) ? value : throw Invalid(field, "a number");
    }

    private decimal ReadNumber(DbfField field)
    {
        if (!IsNumeric(field))
        {
            throw new InvalidCastException($"Field {field.Name} is {field.TypeCode}({field.Length},{field.DecimalCount}) and is not numeric.");
        }

        return ReadDecimal(field) ?? throw NullValue(field);
    }

    private DateTime? ReadDate(DbfField field) =>
        DbfValueParser.TryParseDate(DbfValueParser.Trim(CurrentRecord().AsSpan(field.Offset, field.Length)), field.DayFirst, out var value)
            ? value
            : throw Invalid(field, field.DayFirst ? "a ddMMyyyy date" : "a yyyyMMdd date");

    // Values read as the field's own integer type: a fraction or an out-of-range value means the
    // data does not match the declared type, so it is a format error (the value is not reported).
    private int? ReadInt32(DbfField field)
    {
        if (ReadDecimal(field) is not { } value)
        {
            return null;
        }

        return decimal.Truncate(value) == value && value >= int.MinValue && value <= int.MaxValue
            ? (int)value
            : throw Invalid(field, "a whole number that fits in Int32");
    }

    private long? ReadInt64(DbfField field)
    {
        if (ReadDecimal(field) is not { } value)
        {
            return null;
        }

        return decimal.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue
            ? (long)value
            : throw Invalid(field, "a whole number that fits in Int64");
    }

    private bool? ReadBoolean(DbfField field) =>
        DbfValueParser.TryParseBoolean(DbfValueParser.Trim(CurrentRecord().AsSpan(field.Offset, field.Length)), out var value)
            ? value
            : throw Invalid(field, "a logical value");

    private decimal ToWhole(DbfField field, decimal value) =>
        decimal.Truncate(value) == value
            ? value
            : throw new InvalidCastException($"Field {field.Name} holds a fractional value in record {RecordNumber}; use GetDecimal.");

    // The value itself is deliberately left out of the message: DATASUS data can be personal.
    private FormatException Invalid(DbfField field, string expected)
    {
        var part = _parts[_partIndex].Name is { } name ? $" ({name})" : string.Empty;
        return new($"Field {field.Name} ({field.TypeCode}) of record {RecordNumber}{part} does not hold {expected}.");
    }

    private sealed class DbfColumn : DbColumn
    {
        public DbfColumn(DbfField field, int ordinal, bool allowNull)
        {
            ColumnName = field.Name;
            BaseColumnName = field.Name;
            ColumnOrdinal = ordinal;
            ColumnSize = field.Length;
            DataType = field.FieldType;
            DataTypeName = field.TypeCode.ToString();
            AllowDBNull = allowNull;
            IsReadOnly = true;
            IsLong = false;
            IsKey = false;
            IsUnique = false;
            if (IsNumeric(field))
            {
                NumericPrecision = field.Length;
                NumericScale = field.DecimalCount;
            }
        }
    }
}
