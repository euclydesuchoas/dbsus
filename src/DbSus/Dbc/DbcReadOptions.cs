namespace DbSus.Dbc;

/// <summary>
/// Options for reading a DBC file with <see cref="DbcFile"/>.
/// </summary>
public sealed class DbcReadOptions
{
    /// <summary>Creates options with the default values given on each property.</summary>
    public DbcReadOptions()
    {
    }

    /// <summary>
    /// Gets or sets a value indicating whether the CRC-32 stored in the DBC is checked against the
    /// decompressed DBF. The default is <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// The checksum covers the whole DBF, so a mismatch can only be detected once the last byte has
    /// been decompressed: it surfaces as an <see cref="InvalidDataException"/> from the read that
    /// reaches the end of the data, after every other byte has already been returned. A reader that
    /// stops early never observes it.
    /// </remarks>
    public bool VerifyChecksum { get; set; } = true;

    /// <summary>
    /// Gets or sets a callback invoked when a checksum mismatch is detected, just before the
    /// <see cref="InvalidDataException"/> is thrown. It receives a <see cref="DbcChecksumMismatch"/>
    /// with the part's index and name and the stored and computed checksums, never any data. The
    /// callback runs synchronously, on the thread whose read reached the end of the part. If it
    /// throws, its exception is discarded and the checksum exception is still the one that
    /// propagates. Typical use: marking a cached copy of the file as invalid.
    /// </summary>
    /// <remarks>
    /// A checksum mismatch is reported as a plain <see cref="InvalidDataException"/>, the same type as
    /// other data errors (truncation, invalid compressed data), because that type is sealed and has no
    /// dedicated subclass. This callback is therefore the way to react specifically to a checksum
    /// failure: it is invoked for that case only.
    /// </remarks>
    public Action<DbcChecksumMismatch>? ChecksumMismatch { get; set; }
}
