namespace DbSus.Dbc;

/// <summary>
/// Reads DBC files (compressed dBase files as distributed by DATASUS) as the DBF they contain.
/// </summary>
public static class DbcFile
{
    private const int CopyBufferSize = 81920;

    /// <summary>
    /// Opens a DBC file and returns a read-only stream of the DBF it contains, decompressed on
    /// demand as the stream is read.
    /// </summary>
    /// <param name="path">Path of the DBC file.</param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// A forward-only stream (<see cref="Stream.CanSeek"/> is <see langword="false"/>) that yields the
    /// DBF bytes exactly as stored. Disposing it closes the file.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file is not a valid DBC. Errors in the compressed data surface while reading; see
    /// <see cref="DbcReadOptions.VerifyChecksum"/> for when a checksum mismatch is reported.
    /// </exception>
    public static Stream OpenAsDbf(string path, DbcReadOptions? options = null)
    {
        Guard.NotNull(path, nameof(path));
        options ??= new DbcReadOptions();

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        try
        {
            return new DbcDbfStream(file, leaveOpen: false, options.VerifyChecksum);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns a read-only stream of the DBF contained in a DBC stream, decompressed on demand as
    /// the returned stream is read.
    /// </summary>
    /// <param name="dbc">
    /// A readable stream positioned at the start of the DBC data. It is read sequentially and never
    /// seeked, so network and pipe streams work.
    /// </param>
    /// <param name="options">
    /// Read options, or <see langword="null"/> for the defaults. <see cref="DbcReadOptions.LeaveOpen"/>
    /// controls whether disposing the returned stream also disposes <paramref name="dbc"/>.
    /// </param>
    /// <returns>
    /// A forward-only stream (<see cref="Stream.CanSeek"/> is <see langword="false"/>) that yields the
    /// DBF bytes exactly as stored.
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The data is not a valid DBC. Errors in the compressed data surface while reading; see
    /// <see cref="DbcReadOptions.VerifyChecksum"/> for when a checksum mismatch is reported.
    /// </exception>
    public static Stream OpenAsDbf(Stream dbc, DbcReadOptions? options = null)
    {
        Guard.NotNull(dbc, nameof(dbc));
        if (!dbc.CanRead)
        {
            throw new ArgumentException("The DBC stream must be readable.", nameof(dbc));
        }

        options ??= new DbcReadOptions();
        return new DbcDbfStream(dbc, options.LeaveOpen, options.VerifyChecksum);
    }

    /// <summary>
    /// Converts a DBC file into a DBF file, replacing the destination if it exists.
    /// </summary>
    /// <param name="dbcPath">Path of the DBC file to read.</param>
    /// <param name="dbfPath">Path of the DBF file to write.</param>
    /// <param name="options">Read options, or <see langword="null"/> for the defaults.</param>
    /// <remarks>
    /// The DBF is written to a temporary file in the destination directory and moved into place only
    /// after the whole DBC has been decompressed and, unless disabled, its checksum verified. If the
    /// conversion fails, no partial file is left behind and an existing destination is untouched.
    /// </remarks>
    /// <exception cref="InvalidDataException">The DBC is invalid, corrupt or truncated.</exception>
    public static void ConvertToDbf(string dbcPath, string dbfPath, DbcReadOptions? options = null)
    {
        Guard.NotNull(dbcPath, nameof(dbcPath));
        Guard.NotNull(dbfPath, nameof(dbfPath));

        var destination = Path.GetFullPath(dbfPath);
        var directory = Path.GetDirectoryName(destination) ?? throw new ArgumentException("Invalid DBF path.", nameof(dbfPath));
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var dbf = OpenAsDbf(dbcPath, options))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize))
            {
                dbf.CopyTo(output, CopyBufferSize);
                output.Flush(flushToDisk: true);
            }

            MoveReplacing(temporary, destination);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Converts DBC data into DBF data, writing it to another stream.
    /// </summary>
    /// <param name="dbc">A readable stream positioned at the start of the DBC data; never seeked.</param>
    /// <param name="dbf">A writable stream that receives the DBF bytes.</param>
    /// <param name="options">
    /// Read options, or <see langword="null"/> for the defaults. Neither stream is disposed, so
    /// <see cref="DbcReadOptions.LeaveOpen"/> does not apply.
    /// </param>
    /// <remarks>
    /// Bytes are written to <paramref name="dbf"/> as they are decompressed. If the conversion
    /// fails, what was already written stays in <paramref name="dbf"/>; use
    /// <see cref="ConvertToDbf(string, string, DbcReadOptions?)"/> for an all-or-nothing file.
    /// </remarks>
    /// <exception cref="InvalidDataException">The DBC is invalid, corrupt or truncated.</exception>
    public static void ConvertToDbf(Stream dbc, Stream dbf, DbcReadOptions? options = null)
    {
        Guard.NotNull(dbc, nameof(dbc));
        Guard.NotNull(dbf, nameof(dbf));
        if (!dbf.CanWrite)
        {
            throw new ArgumentException("The DBF stream must be writable.", nameof(dbf));
        }

        var verifyChecksum = options?.VerifyChecksum ?? true;
        using var source = OpenAsDbf(dbc, new DbcReadOptions { LeaveOpen = true, VerifyChecksum = verifyChecksum });
        source.CopyTo(dbf, CopyBufferSize);
    }

    private static void MoveReplacing(string source, string destination)
    {
#if NET
        File.Move(source, destination, overwrite: true);
#else
        if (File.Exists(destination))
        {
            File.Replace(source, destination, destinationBackupFileName: null);
        }
        else
        {
            File.Move(source, destination);
        }
#endif
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the original exception is the one worth reporting.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
