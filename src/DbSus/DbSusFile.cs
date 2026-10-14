using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus;

/// <summary>
/// Entry point that combines the DBC and DBF layers: opens DATASUS files as a
/// <see cref="DbfReader"/>, decompressing DBC files on the fly, and reads tables split into
/// parts (SIASUS-PA's <c>a</c>, <c>b</c>, <c>c</c>, ... files) as one.
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
        return OpenByExtension([path], nameof(path), dbfOptions, dbcOptions);
    }

    /// <summary>
    /// Opens the parts of a split table as one reader, in the order given, choosing DBC or DBF for
    /// each part by its extension. See <see cref="FindParts(string)"/> to list the parts.
    /// </summary>
    /// <param name="paths">Paths of the parts, in reading order.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>; they apply to every part.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>; they apply to every DBC part.</param>
    /// <returns>
    /// A reader positioned before the first record, after every part's header has been read and
    /// checked. Disposing it closes every file.
    /// </returns>
    /// <exception cref="ArgumentException">No path is given, or an extension is neither <c>.dbc</c> nor <c>.dbf</c>.</exception>
    /// <exception cref="InvalidDataException">A header is invalid, or the parts do not share the same schema.</exception>
    public static DbfReader OpenReader(IEnumerable<string> paths, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null) =>
        OpenByExtension(DbfReader.RequireParts(paths, nameof(paths)), nameof(paths), dbfOptions, dbcOptions);

    /// <summary>
    /// Opens a DBC file, whatever its extension, as a reader over the DBF it contains.
    /// </summary>
    /// <param name="path">Path of the DBC file.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/> for the defaults.</param>
    /// <returns>
    /// A reader positioned before the first record. Disposing it closes the file. Unless checksum
    /// verification is disabled, the <see cref="DbfReader.Read"/> that reaches the end throws
    /// <see cref="InvalidDataException"/> if the data does not match the DBC's CRC-32. To tell a
    /// checksum failure apart from other data errors, use <see cref="DbcReadOptions.ChecksumMismatch"/>.
    /// </returns>
    public static DbfReader OpenDbcReader(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        Guard.NotNull(path, nameof(path));
        return OpenDbcReader([path], dbfOptions, dbcOptions);
    }

    /// <summary>
    /// Opens the DBC parts of a split table, whatever their extension, as one reader, in the order given.
    /// </summary>
    /// <param name="paths">Paths of the DBC parts, in reading order (see <see cref="FindParts(string)"/>).</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>; they apply to every part.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>; they apply to every part.</param>
    /// <returns>
    /// A reader positioned before the first record, after every part's header has been read and
    /// checked. Each part's checksum is verified when that part has been read to its end.
    /// </returns>
    public static DbfReader OpenDbcReader(IEnumerable<string> paths, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        var list = DbfReader.RequireParts(paths, nameof(paths));
        return OpenPaths(list, dbcOptions, dbfOptions, _ => true);
    }

    /// <summary>
    /// Opens a DBC stream as a reader over the DBF it contains.
    /// </summary>
    /// <param name="dbc">A readable stream positioned at the start of the DBC data; never seeked.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to hand <paramref name="dbc"/> over: it is disposed with
    /// the reader, and also if this method throws. <see langword="true"/> to keep it yours: it is
    /// never disposed.
    /// </param>
    /// <returns>
    /// A reader positioned before the first record. Unless checksum verification is disabled, the
    /// <see cref="DbfReader.Read"/> that reaches the end throws <see cref="InvalidDataException"/>
    /// if the data does not match the DBC's CRC-32. To tell a checksum failure apart from other data
    /// errors, use <see cref="DbcReadOptions.ChecksumMismatch"/>.
    /// </returns>
    public static DbfReader OpenDbcReader(Stream dbc, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, bool leaveOpen = false)
    {
        Guard.NotNull(dbc, nameof(dbc));
        return OpenDbcReader([dbc], dbfOptions, dbcOptions, leaveOpen);
    }

    /// <summary>
    /// Opens the DBC parts of a split table as one reader, in the order given. Each part is a stream
    /// with an optional name, used in error messages and passed to
    /// <see cref="DbcReadOptions.ChecksumMismatch"/>.
    /// </summary>
    /// <param name="parts">
    /// The parts, in reading order. Every header is read before this method returns, so every
    /// stream must be open at once. A part without a name is called <c>part N</c> in messages.
    /// </param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>; they apply to every part.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>; they apply to every part.</param>
    /// <param name="leaveOpen">
    /// <see langword="false"/> (the default) to hand the streams over: each is disposed once its part
    /// has been read or the reader is disposed, and all of them if this method throws.
    /// <see langword="true"/> to keep them yours: they are never disposed.
    /// </param>
    /// <returns>A reader positioned before the first record.</returns>
    /// <exception cref="ArgumentException">No part is given, a part is null, or a stream is not readable.</exception>
    /// <exception cref="InvalidDataException">A header is invalid, or the parts do not share the same schema.</exception>
    public static DbfReader OpenDbcReader(IEnumerable<DbcPart> parts, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, bool leaveOpen = false)
    {
        Guard.NotNull(parts, nameof(parts));
        var given = parts.ToList();
        var streams = given.Where(part => part is not null).Select(part => part.Stream).ToList();
        return OpenDbcStreams(streams, leaveOpen, () =>
        {
            var list = DbfReader.RequireParts(given, nameof(parts));
            return (
                list.Select(part => part.Stream).ToList(),
                list.Select((part, i) => part.Name ?? (list.Count == 1 ? null : $"part {i + 1}")).ToList());
        }, dbfOptions, dbcOptions);
    }

    /// <summary>Opens unnamed DBC streams of a split table (<c>part N</c> in messages); used by tests.</summary>
    internal static DbfReader OpenDbcReader(IEnumerable<Stream> parts, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, bool leaveOpen = false)
    {
        Guard.NotNull(parts, nameof(parts));
        var given = parts.ToList();
        return OpenDbcStreams(given, leaveOpen, () =>
        {
            var list = DbfReader.RequireParts(given, nameof(parts));
            return (list, list.Select((_, i) => list.Count == 1 ? null : $"part {i + 1}").ToList());
        }, dbfOptions, dbcOptions);
    }

    /// <summary>
    /// Lists the parts of a split DATASUS table on disk, in order, from the path of any part or of
    /// the unsplit name (e.g. <c>PAMG2407a.dbc</c> or <c>PAMG2407.dbc</c> → <c>PAMG2407a.dbc</c>,
    /// <c>PAMG2407b.dbc</c>, ...). A table that is not split returns its single file.
    /// </summary>
    /// <param name="path">
    /// Path of a part or of the unsplit name. The file name must end in digits, optionally followed
    /// by one part letter, as DATASUS names do; only files with the same extension are considered.
    /// </param>
    /// <returns>Full paths of the parts, ordered by part letter.</returns>
    /// <remarks>
    /// <para>
    /// Names are matched ignoring case on every operating system, so Windows and Linux behave the
    /// same; two files that differ only by case are reported as ambiguous.
    /// </para>
    /// <para>
    /// Parts must be contiguous from <c>a</c>: a gap is an error. A missing <em>last</em> part cannot be
    /// detected from the local folder alone (nothing says how many parts exist); the DATASUS FTP
    /// listing, used by <c>DbSus.Client</c>, is the source of truth for that.
    /// </para>
    /// <para>
    /// An unsplit file next to lettered parts of the same name (e.g. <c>PAMG2407.dbc</c> and
    /// <c>PAMG2407a.dbc</c>) is an error: the FTP never publishes both, so it signals a folder that
    /// mixes versions, and guessing could read the wrong data.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The file name does not follow the DATASUS naming.</exception>
    /// <exception cref="FileNotFoundException">No matching file exists, or a part before the last one is missing.</exception>
    /// <exception cref="IOException">The folder holds ambiguous names (case variants, or unsplit and split files together).</exception>
    public static IReadOnlyList<string> FindParts(string path)
    {
        Guard.NotNull(path, nameof(path));
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("Invalid path.", nameof(path));
        var (baseName, extension) = PartNaming.Parse(Path.GetFileName(fullPath), nameof(path));
        var names = Directory.EnumerateFiles(directory).Select(file => Path.GetFileName(file)!);
        var parts = PartNaming.Select(
            names,
            baseName,
            extension,
            (missing, message) => new FileNotFoundException(message, Path.Combine(directory, missing)));
        return [.. parts.Select(name => Path.Combine(directory, name))];
    }

    private static DbfReader OpenByExtension(List<string> list, string paramName, DbfReadOptions? dbfOptions, DbcReadOptions? dbcOptions)
    {
        return OpenPaths(list, dbcOptions, dbfOptions, path =>
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".dbc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(extension, ".dbf", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new ArgumentException(
                $"Cannot tell the format of '{Path.GetFileName(path)}' from its extension; use OpenDbcReader or DbfReader.Open.",
                paramName);
        });
    }

    private static DbfReader OpenPaths(List<string> paths, DbcReadOptions? dbcOptions, DbfReadOptions? dbfOptions, Func<string, bool> isDbc)
    {
        var prepared = new List<DbfPart>();
        try
        {
            foreach (var path in paths)
            {
                Stream stream = isDbc(path)
                    ? DbcFile.OpenAsDbf(path, dbcOptions, prepared.Count)
                    : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
                prepared.Add(new DbfPart(Path.GetFileName(path), stream, [stream], [stream], drainAtEnd: isDbc(path)));
            }
        }
        catch
        {
            DbfPart.DisposeCreated(prepared);
            throw;
        }

        return DbfReader.Create(prepared, dbfOptions);
    }

    // Validates (inside the try, so a caller's streams handed over with leaveOpen: false are disposed
    // even when the arguments are rejected), wraps each stream in a decompressing part and opens.
    private static DbfReader OpenDbcStreams(
        List<Stream> given,
        bool leaveOpen,
        Func<(List<Stream> Streams, List<string?> Names)> validate,
        DbfReadOptions? dbfOptions,
        DbcReadOptions? dbcOptions)
    {
        var prepared = new List<DbfPart>();
        try
        {
            var (streams, names) = validate();
            for (var i = 0; i < streams.Count; i++)
            {
                // The decompressing stream never disposes the caller's stream itself; the part
                // disposes it once read, unless leaveOpen.
                var dbf = DbcFile.OpenAsDbfPart(streams[i], dbcOptions, i, names[i]);
                prepared.Add(new DbfPart(names[i], dbf, leaveOpen ? [dbf] : [dbf, streams[i]], [dbf], drainAtEnd: true));
            }

            return DbfReader.Create(prepared, dbfOptions);
        }
        catch
        {
            DbfPart.DisposeCreated(prepared);
            if (!leaveOpen)
            {
                DbfReader.DisposeStreams(given);
            }

            throw;
        }
    }
}
