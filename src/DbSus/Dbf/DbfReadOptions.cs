using System.Text;

namespace DbSus.Dbf;

/// <summary>
/// Options for reading a DBF table with <see cref="DbfReader"/>.
/// </summary>
public sealed class DbfReadOptions
{
    /// <summary>
    /// Gets or sets the encoding of character fields. When set, it takes precedence over the
    /// language driver ID (LDID) stored in the header.
    /// </summary>
    public Encoding? Encoding { get; set; }

    /// <summary>
    /// Gets or sets the encoding used when <see cref="Encoding"/> is not set and the header's
    /// LDID is 0 or unknown. When <see langword="null"/>, code page 1252 is used.
    /// </summary>
    /// <remarks>
    /// Code page 1252 is the default because it is the only encoding confirmed so far in DATASUS
    /// files with accented text, including files whose LDID is 0. This remains to be validated
    /// across more sources.
    /// </remarks>
    public Encoding? FallbackEncoding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether disposing the reader leaves the stream passed to
    /// <see cref="DbfReader.Open(Stream, DbfReadOptions?)"/> open. The default is
    /// <see langword="false"/>. Readers opened by path own their files, and readers opened from a
    /// DBC through <c>DbSus.DbSusFile</c> follow <c>DbcReadOptions.LeaveOpen</c> instead.
    /// </summary>
    public bool LeaveOpen { get; set; }

    /// <summary>
    /// Gets or sets the names of the fields to expose, in the order given (a projection). Ordinals
    /// refer to this list. When <see langword="null"/>, all fields are exposed in file order.
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
}
