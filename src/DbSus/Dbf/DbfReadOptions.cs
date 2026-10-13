using System.Text;

namespace DbSus.Dbf;

/// <summary>
/// Options for reading a DBF table with <see cref="DbfReader"/>.
/// </summary>
public sealed class DbfReadOptions
{
    /// <summary>Creates options with the default values given on each property.</summary>
    public DbfReadOptions()
    {
    }

    /// <summary>
    /// Gets or sets the encoding of character fields. When set, it takes precedence over the
    /// language driver ID (LDID) stored in the header.
    /// </summary>
    /// <remarks>
    /// The encodings DbSus picks from the LDID need no setup. To pass a code-page encoding yourself
    /// (for example code page 850): on .NET Framework, <c>Encoding.GetEncoding(850)</c> works as is;
    /// on .NET (Core) and .NET 5 or later, code pages are not built in and
    /// <c>Encoding.GetEncoding(850)</c> throws <see cref="NotSupportedException"/>, so take the
    /// encoding from <c>CodePagesEncodingProvider.Instance.GetEncoding(850)</c> (no process-wide
    /// registration needed), or call <c>Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)</c>
    /// once at startup. DbSus itself never registers a provider.
    /// </remarks>
    public Encoding? Encoding { get; set; }

    /// <summary>
    /// Gets or sets the encoding used when <see cref="Encoding"/> is not set and the header's
    /// LDID is 0 or unknown. When <see langword="null"/>, code page 1252 is used.
    /// </summary>
    /// <remarks>
    /// Code page 1252 is the default. What was counted in DATASUS files in October 2026: text
    /// marked with LDID 0x02 reads as code page 850 in one file, and text marked 0x03 as code
    /// page 1252 in two, as declared; for LDID 0, the evidence is four bytes in one CNES file,
    /// which read as code page 1252; in the files marked 0x57, 0x58, 0x35 and 0x25, no text with
    /// a byte above 0x7F was seen, so their code page is not validated. See
    /// <see cref="Encoding"/> for how to obtain a code-page encoding.
    /// </remarks>
    public Encoding? FallbackEncoding { get; set; }

    /// <summary>
    /// Gets or sets the names of the fields to expose, in the order given (a projection). Ordinals
    /// refer to this list. When <see langword="null"/>, all fields are exposed in file order. An
    /// empty list is rejected when the reader opens (<see cref="ArgumentException"/>).
    /// </summary>
    public IReadOnlyList<string>? Columns { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether records marked as deleted are returned. The default
    /// is <see langword="false"/> (deleted records are skipped).
    /// </summary>
    public bool IncludeDeletedRecords { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an all-blank character field is returned as
    /// <see cref="DBNull"/> instead of an empty string. The default is <see langword="false"/>.
    /// </summary>
    public bool BlankStringsAsNull { get; set; }

    /// <summary>
    /// Gets or sets what the reader does with the content of a numeric (<c>N</c> or <c>F</c>) or
    /// date (<c>D</c>) field that is not a number or a date. The default is
    /// <see cref="DbfUnconvertibleValueHandling.Throw"/>: <see cref="FormatException"/>. A value
    /// outside the enumeration is rejected when the reader opens
    /// (<see cref="ArgumentOutOfRangeException"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// With <see cref="DbfUnconvertibleValueHandling.AsterisksAsNull"/>, a field filled with
    /// asterisks is read as null and any other content that does not convert still throws; with
    /// <see cref="DbfUnconvertibleValueHandling.Null"/>, any content that does not convert is read
    /// as null. Read as null means what a blank field means: <see cref="DbfReader.IsDBNull(int)"/>
    /// returns <see langword="true"/>, <see cref="DbfReader.GetValue(int)"/> and
    /// <see cref="DbfReader.GetValues(object[])"/> give <see cref="DBNull"/>,
    /// <see cref="DbfReader.GetFieldValue{T}(int)"/> of a nullable type gives
    /// <see langword="null"/>, and the typed getters throw <see cref="InvalidCastException"/>.
    /// <see cref="DbfReader.GetRawBytes(int)"/> returns the bytes of the field in every mode.
    /// </para>
    /// <para>
    /// It does not apply to logical (<c>L</c>) fields, nor to the character fields of
    /// <see cref="Conversions"/>: those throw <see cref="FormatException"/> in every mode.
    /// </para>
    /// </remarks>
    public DbfUnconvertibleValueHandling UnconvertibleValues { get; set; }

    /// <summary>
    /// Gets the opt-in conversions of character (<c>C</c>) fields into typed values, keyed by field
    /// name (case-insensitive). Example: <c>Conversions["DTOBITO"] = DbfFieldConversion.Date("ddMMyyyy")</c>.
    /// Each name must be a <c>C</c> field of the file, or opening fails with <see cref="ArgumentException"/>.
    /// </summary>
    public IDictionary<string, DbfFieldConversion> Conversions { get; } =
        new Dictionary<string, DbfFieldConversion>(StringComparer.OrdinalIgnoreCase);
}
