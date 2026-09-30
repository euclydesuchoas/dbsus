using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus;

/// <summary>
/// Entry point that combines the DBC and DBF layers: opens a DATASUS file as a
/// <see cref="DbfReader"/>, decompressing DBC files on the fly.
/// </summary>
public static class DbSusFile
{
    /// <summary>
    /// Opens a <c>.dbc</c> or <c>.dbf</c> file as a reader, choosing by the extension (ignoring
    /// case). Use <see cref="OpenDbcReader(string, DbfReadOptions?, DbcReadOptions?)"/> or
    /// <see cref="DbfReader.Open(string, DbfReadOptions?)"/> when the extension is not reliable.
    /// </summary>
    /// <param name="path">Path of the file.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="dbcOptions">DBC read options (used for <c>.dbc</c> files), or <see langword="null"/>.</param>
    /// <returns>A reader positioned before the first record. Disposing it closes the file.</returns>
    /// <exception cref="ArgumentException">The extension is neither <c>.dbc</c> nor <c>.dbf</c>.</exception>
    public static DbfReader OpenReader(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        Guard.NotNull(path, nameof(path));
        var extension = Path.GetExtension(path);
        if (string.Equals(extension, ".dbc", StringComparison.OrdinalIgnoreCase))
        {
            return OpenDbcReader(path, dbfOptions, dbcOptions);
        }

        if (string.Equals(extension, ".dbf", StringComparison.OrdinalIgnoreCase))
        {
            return DbfReader.Open(path, dbfOptions);
        }

        throw new ArgumentException(
            $"Cannot tell the format of '{Path.GetFileName(path)}' from its extension; use OpenDbcReader or DbfReader.Open.",
            nameof(path));
    }

    /// <summary>
    /// Opens a DBC file, whatever its extension, as a reader over the DBF it contains.
    /// </summary>
    /// <param name="path">Path of the DBC file.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// A reader positioned before the first record. Disposing it closes the file. Unless checksum
    /// verification is disabled, the <see cref="DbfReader.Read"/> that reaches the end throws
    /// <see cref="InvalidDataException"/> if the data does not match the DBC's CRC-32.
    /// </returns>
    public static DbfReader OpenDbcReader(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        Guard.NotNull(path, nameof(path));
        var dbf = DbcFile.OpenAsDbf(path, dbcOptions);
        try
        {
            return DbfReader.OpenDecompressed(dbf, dbfOptions, [dbf]);
        }
        catch
        {
            dbf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a DBC stream as a reader over the DBF it contains.
    /// </summary>
    /// <param name="dbc">A readable stream positioned at the start of the DBC data; never seeked.</param>
    /// <param name="dbfOptions">
    /// DBF read options, or <see langword="null"/> for the defaults. Its
    /// <see cref="DbfReadOptions.LeaveOpen"/> does not apply here.
    /// </param>
    /// <param name="dbcOptions">
    /// DBC read options, or <see langword="null"/> for the defaults. Its
    /// <see cref="DbcReadOptions.LeaveOpen"/> controls whether disposing the reader also disposes
    /// <paramref name="dbc"/>. If this method throws, <paramref name="dbc"/> is not disposed.
    /// </param>
    /// <returns>
    /// A reader positioned before the first record. Unless checksum verification is disabled, the
    /// <see cref="DbfReader.Read"/> that reaches the end throws <see cref="InvalidDataException"/>
    /// if the data does not match the DBC's CRC-32.
    /// </returns>
    public static DbfReader OpenDbcReader(Stream dbc, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        Guard.NotNull(dbc, nameof(dbc));
        var leaveOpen = dbcOptions?.LeaveOpen ?? false;
        var verifyChecksum = dbcOptions?.VerifyChecksum ?? true;

        // The decompressing stream never disposes the caller's stream itself, so a failure while
        // opening leaves it untouched; the reader disposes it later unless LeaveOpen is set.
        var dbf = DbcFile.OpenAsDbf(dbc, new DbcReadOptions { LeaveOpen = true, VerifyChecksum = verifyChecksum });
        try
        {
            return DbfReader.OpenDecompressed(dbf, dbfOptions, leaveOpen ? [dbf] : [dbf, dbc]);
        }
        catch
        {
            dbf.Dispose();
            throw;
        }
    }
}
