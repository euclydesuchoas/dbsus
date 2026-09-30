namespace DbSus.Dbc;

/// <summary>
/// Options for reading a DBC file with <see cref="DbcFile"/>.
/// </summary>
public sealed class DbcReadOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the stream returned by
    /// <see cref="DbcFile.OpenAsDbf(Stream, DbcReadOptions?)"/> leaves the DBC stream open when it is
    /// disposed. The default is <see langword="false"/>. Methods that take file paths own the files
    /// they open, and <see cref="DbcFile.ConvertToDbf(Stream, Stream, DbcReadOptions?)"/> never
    /// disposes the streams it is given, so neither uses this option.
    /// </summary>
    public bool LeaveOpen { get; set; }

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
}
