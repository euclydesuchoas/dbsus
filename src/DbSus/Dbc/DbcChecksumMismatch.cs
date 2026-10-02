namespace DbSus.Dbc;

/// <summary>
/// Describes a DBC checksum mismatch, passed to <see cref="DbcReadOptions.ChecksumMismatch"/> just
/// before the <see cref="InvalidDataException"/> is thrown. It carries no data from the file.
/// </summary>
public sealed class DbcChecksumMismatch
{
    internal DbcChecksumMismatch(int partIndex, string? partName, uint expectedChecksum, uint actualChecksum)
    {
        PartIndex = partIndex;
        PartName = partName;
        ExpectedChecksum = expectedChecksum;
        ActualChecksum = actualChecksum;
    }

    /// <summary>Gets the zero-based index of the part whose checksum failed (0 for a single file).</summary>
    public int PartIndex { get; }

    /// <summary>
    /// Gets the name of the part: the file name when opened by path, the caller's name for named
    /// streams, <c>part N</c> for unnamed streams of a split table, and <see langword="null"/> for a
    /// single unnamed stream.
    /// </summary>
    public string? PartName { get; }

    /// <summary>Gets the CRC-32 stored in the DBC.</summary>
    public uint ExpectedChecksum { get; }

    /// <summary>Gets the CRC-32 of the decompressed DBF.</summary>
    public uint ActualChecksum { get; }
}
