namespace DbSus.Dbf;

/// <summary>Tells where the encoding of a <see cref="DbfReader"/> came from.</summary>
public enum DbfEncodingSource
{
    /// <summary>
    /// <see cref="DbfReadOptions.Encoding"/> was set, and it is the encoding used. The header's
    /// language driver ID (LDID) is not consulted, even when it is a known one.
    /// </summary>
    Option = 0,

    /// <summary>
    /// The header's language driver ID (LDID) is a known one, and the encoding is the code page it
    /// declares.
    /// </summary>
    LanguageDriver = 1,

    /// <summary>
    /// The header gave no encoding, so <see cref="DbfReadOptions.FallbackEncoding"/> was used, or
    /// code page 1252 when it is not set. This covers three cases: the language driver ID (LDID)
    /// is 0x00 (no driver); the LDID is not in the library's map; the LDID is in the map, but this
    /// runtime does not have its code page. <see cref="DbfHeader.LanguageDriverId"/> shows which.
    /// </summary>
    Fallback = 2,
}
