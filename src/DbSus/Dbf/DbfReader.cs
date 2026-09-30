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
/// A reader is not thread-safe. The asynchronous methods inherited from
/// <see cref="DbDataReader"/> (such as <c>ReadAsync</c>) currently complete synchronously.
/// </para>
/// <para>
/// Value mapping: <c>C</c> is a <see cref="string"/> without trailing spaces (all blank is an
/// empty string, or <see cref="DBNull"/> with <see cref="DbfReadOptions.BlankStringsAsNull"/>);
/// <c>N</c> without decimals is an <see cref="int"/> up to 9 digits wide, a <see cref="long"/> up
/// to 18 and a <see cref="decimal"/> beyond; <c>N</c> with decimals and <c>F</c> are
/// <see cref="decimal"/>; <c>D</c> is a <see cref="DateTime"/> (<c>YYYYMMDD</c>); <c>L</c> is a
/// <see cref="bool"/>. Blank numbers, dates and logicals, and dates made of zeros, are
/// <see cref="DBNull"/>. Content that does not parse throws <see cref="FormatException"/>; the
/// message names the field and the record but, deliberately, not the value.
/// </para>
/// </remarks>
[SuppressMessage("Design", "CA1010", Justification = "The non-generic IEnumerable comes from DbDataReader; its items are records, as in every ADO.NET reader.")]
public sealed class DbfReader : DbDataReader, IDbColumnSchemaGenerator
{
    private const int TableHeaderLength = 32;
    private const byte DeletedFlag = (byte)'*';
    private const byte Space = (byte)' ';

    private readonly Stream _stream;
    private readonly IDisposable[] _ownedResources;
    private readonly bool _drainAtEnd;
    private readonly bool _includeDeleted;
    private readonly bool _blankStringsAsNull;
    private readonly DbfField[] _fields;
    private readonly Dictionary<string, int> _ordinals;
    private readonly byte[] _record;
    private long _recordsConsumed;
    private bool _hasRecord;
    private bool _closed;

    private DbfReader(Stream stream, DbfReadOptions options, IDisposable[] ownedResources, bool drainAtEnd)
    {
        _stream = stream;
        _ownedResources = ownedResources;
        _drainAtEnd = drainAtEnd;
        _includeDeleted = options.IncludeDeletedRecords;
        _blankStringsAsNull = options.BlankStringsAsNull;

        Header = ReadHeader(stream);
        Encoding = DbfEncodings.Resolve(Header.LanguageDriverId, options);
        _fields = Project(Header.Fields, options.Columns);
        Fields = new ReadOnlyCollection<DbfField>(_fields);
        // Backwards, so that with duplicate names the first field wins.
        _ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = _fields.Length - 1; i >= 0; i--)
        {
            _ordinals[_fields[i].Name] = i;
        }

        _record = new byte[Header.RecordLength];
    }

    /// <summary>Gets the table header.</summary>
    public DbfHeader Header { get; }

    /// <summary>
    /// Gets the fields this reader exposes, in ordinal order: all fields in file order, or the
    /// projection given in <see cref="DbfReadOptions.Columns"/>.
    /// </summary>
    public IReadOnlyList<DbfField> Fields { get; }

    /// <summary>Gets the encoding used for character fields.</summary>
    public Encoding Encoding { get; }

    /// <summary>
    /// Gets the 1-based physical number of the current record in the file (deleted records count),
    /// or 0 before the first <see cref="Read"/>.
    /// </summary>
    public long RecordNumber => _hasRecord ? _recordsConsumed : 0;

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
    public override bool HasRows => Header.RecordCount > 0;

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
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        try
        {
            return new DbfReader(file, options ?? new DbfReadOptions(), [file], drainAtEnd: false);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>Opens a DBF table from a stream.</summary>
    /// <param name="dbf">
    /// A readable stream positioned at the start of the DBF data. It is read sequentially and never
    /// seeked. The header is read before this method returns, so it blocks until the header arrives.
    /// </param>
    /// <param name="options">
    /// Read options, or <see langword="null"/> for the defaults. <see cref="DbfReadOptions.LeaveOpen"/>
    /// controls whether disposing the reader also disposes <paramref name="dbf"/>. If this method
    /// throws, <paramref name="dbf"/> is not disposed.
    /// </param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="InvalidDataException">The header is invalid.</exception>
    /// <exception cref="ArgumentException">A projected column does not exist or is repeated.</exception>
    public static DbfReader Open(Stream dbf, DbfReadOptions? options = null)
    {
        Guard.NotNull(dbf, nameof(dbf));
        if (!dbf.CanRead)
        {
            throw new ArgumentException("The DBF stream must be readable.", nameof(dbf));
        }

        options ??= new DbfReadOptions();
        return new DbfReader(dbf, options, options.LeaveOpen ? [] : [dbf], drainAtEnd: false);
    }

    /// <summary>
    /// Opens a reader over a DBF stream decompressed from a DBC. Once the last record has been
    /// read, the rest of the stream is drained so that the DBC checksum is verified; a mismatch
    /// surfaces as an <see cref="InvalidDataException"/> from that final <see cref="Read"/>.
    /// </summary>
    internal static DbfReader OpenDecompressed(Stream dbf, DbfReadOptions? options, IDisposable[] ownedResources) =>
        new(dbf, options ?? new DbfReadOptions(), ownedResources, drainAtEnd: true);

    /// <summary>Advances to the next record, skipping deleted ones unless configured otherwise.</summary>
    /// <returns><see langword="true"/> if there is a current record; <see langword="false"/> at the end.</returns>
    /// <exception cref="InvalidDataException">The data ends before the declared number of records.</exception>
    public override bool Read()
    {
        Guard.NotDisposed(_closed, this);
        while (_recordsConsumed < Header.RecordCount)
        {
            ReadRecord();
            _recordsConsumed++;
            if (_includeDeleted || _record[0] != DeletedFlag)
            {
                _hasRecord = true;
                return true;
            }
        }

        if (_hasRecord || _recordsConsumed == Header.RecordCount)
        {
            _hasRecord = false;
            if (_drainAtEnd)
            {
                Drain();
            }
        }

        return false;
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
                return DbfValueParser.TryParseDate(DbfValueParser.Trim(bytes), out var date) && date is null;
            case DbfValueKind.Boolean:
                return DbfValueParser.TryParseBoolean(DbfValueParser.Trim(bytes), out var flag) && flag is null;
            default:
                return false;
        }
    }

    /// <summary>Returns whether a field of the current record is null, by field name.</summary>
    public bool IsDBNull(string name) => IsDBNull(GetOrdinal(name));

    /// <inheritdoc/>
    public override object GetValue(int ordinal)
    {
        var field = Field(ordinal);
        switch (field.Kind)
        {
            case DbfValueKind.String:
                return ReadString(field) ?? (object)DBNull.Value;
            case DbfValueKind.Int32:
                return ReadDecimal(field) is { } int32 ? (object)ToInt32(field, int32) : DBNull.Value;
            case DbfValueKind.Int64:
                return ReadDecimal(field) is { } int64 ? (object)ToInt64(field, int64) : DBNull.Value;
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
    /// <exception cref="InvalidCastException">The field is not numeric, the value is null or has a fraction.</exception>
    /// <exception cref="OverflowException">The value does not fit.</exception>
    public override int GetInt32(int ordinal)
    {
        var field = Field(ordinal);
        return checked((int)ToWhole(field, ReadNumber(field)));
    }

    /// <summary>Returns a numeric field as a 32-bit integer, by field name.</summary>
    public int GetInt32(string name) => GetInt32(GetOrdinal(name));

    /// <summary>Returns a numeric field as a 64-bit integer.</summary>
    /// <exception cref="InvalidCastException">The field is not numeric, the value is null or has a fraction.</exception>
    /// <exception cref="OverflowException">The value does not fit.</exception>
    public override long GetInt64(int ordinal)
    {
        var field = Field(ordinal);
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
    public override T GetFieldValue<T>(int ordinal)
    {
        if (IsDBNull(ordinal))
        {
            return default(T) is null ? default! : throw NullValue(Field(ordinal));
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object value =
            type == typeof(int) ? GetInt32(ordinal)
            : type == typeof(long) ? GetInt64(ordinal)
            : type == typeof(decimal) ? GetDecimal(ordinal)
            : type == typeof(double) ? GetDouble(ordinal)
            : type == typeof(float) ? GetFloat(ordinal)
            : type == typeof(short) ? GetInt16(ordinal)
            : type == typeof(byte) ? GetByte(ordinal)
            : type == typeof(string) ? GetString(ordinal)
            : type == typeof(DateTime) ? GetDateTime(ordinal)
            : type == typeof(bool) ? GetBoolean(ordinal)
            : type == typeof(byte[]) ? GetRawBytes(ordinal).ToArray()
            : GetValue(ordinal);
        return (T)value;
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
        foreach (var resource in _ownedResources)
        {
            resource.Dispose();
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

    private static DbfHeader ReadHeader(Stream stream)
    {
        var prefix = new byte[TableHeaderLength];
        if (ReadFully(stream, prefix, 0, prefix.Length) != prefix.Length)
        {
            throw new InvalidDataException("Invalid DBF: the data ends inside the 32-byte table header.");
        }

        var headerLength = prefix[8] | (prefix[9] << 8);
        if (headerLength < TableHeaderLength)
        {
            throw new InvalidDataException($"Invalid DBF: the header length is {headerLength}, below 32.");
        }

        var header = new byte[headerLength];
        Buffer.BlockCopy(prefix, 0, header, 0, prefix.Length);
        if (ReadFully(stream, header, prefix.Length, headerLength - prefix.Length) != headerLength - prefix.Length)
        {
            throw new InvalidDataException("Invalid DBF: the data ends inside the header.");
        }

        return DbfHeader.Parse(header);
    }

    private static DbfField[] Project(IReadOnlyList<DbfField> fields, IReadOnlyList<string>? columns)
    {
        if (columns is null)
        {
            return [.. fields];
        }

        var byName = new Dictionary<string, DbfField>(StringComparer.OrdinalIgnoreCase);
        for (var i = fields.Count - 1; i >= 0; i--)
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

    private void ReadRecord()
    {
        var read = ReadFully(_stream, _record, 0, _record.Length);
        if (read != _record.Length)
        {
            _hasRecord = false;
            throw new InvalidDataException(
                $"The DBF data ends inside record {_recordsConsumed + 1} of {Header.RecordCount}.");
        }
    }

    // Reads the rest of the stream (normally just the 0x1A marker) so a decompressing stream
    // reaches its end and verifies its checksum.
    private void Drain()
    {
        var buffer = new byte[4096];
        while (_stream.Read(buffer, 0, buffer.Length) > 0)
        {
        }
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
        DbfValueParser.TryParseDate(DbfValueParser.Trim(CurrentRecord().AsSpan(field.Offset, field.Length)), out var value)
            ? value
            : throw Invalid(field, "a YYYYMMDD date");

    private bool? ReadBoolean(DbfField field) =>
        DbfValueParser.TryParseBoolean(DbfValueParser.Trim(CurrentRecord().AsSpan(field.Offset, field.Length)), out var value)
            ? value
            : throw Invalid(field, "a logical value");

    private decimal ToWhole(DbfField field, decimal value) =>
        decimal.Truncate(value) == value
            ? value
            : throw new InvalidCastException($"Field {field.Name} holds a fractional value in record {RecordNumber}; use GetDecimal.");

    private int ToInt32(DbfField field, decimal value) => checked((int)ToWhole(field, value));

    private long ToInt64(DbfField field, decimal value) => checked((long)ToWhole(field, value));

    // The value itself is deliberately left out of the message: DATASUS data can be personal.
    private FormatException Invalid(DbfField field, string expected) =>
        new($"Field {field.Name} ({field.TypeCode}) of record {RecordNumber} does not hold {expected}.");

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
