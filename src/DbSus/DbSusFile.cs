using System.Text.RegularExpressions;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus;

/// <summary>
/// Entry point that combines the DBC and DBF layers: opens DATASUS files as a
/// <see cref="DbfReader"/>, decompressing DBC files on the fly, and reads tables split into
/// parts (SIA-PA's <c>a</c>, <c>b</c>, <c>c</c>, ... files) as one.
/// </summary>
public static class DbSusFile
{
    private static readonly Regex PartName = new(@"^(?<base>.*\d)(?<part>[A-Za-z])?$", RegexOptions.CultureInvariant);

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
    public static DbfReader OpenReader(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null) =>
        OpenReader([path], dbfOptions, dbcOptions);

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
    public static DbfReader OpenReader(IEnumerable<string> paths, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        var list = DbfReader.RequireParts(paths, nameof(paths));
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
                nameof(paths));
        });
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
    public static DbfReader OpenDbcReader(string path, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null) =>
        OpenDbcReader([path], dbfOptions, dbcOptions);

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
    public static DbfReader OpenDbcReader(Stream dbc, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null) =>
        OpenDbcReader([dbc], dbfOptions, dbcOptions);

    /// <summary>
    /// Opens the DBC streams of a split table as one reader, in the order given.
    /// </summary>
    /// <param name="parts">
    /// Readable streams positioned at the start of each DBC part; never seeked. Every header is read
    /// before this method returns, so every stream must be open at once. If it throws, no stream is disposed.
    /// </param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>; its <see cref="DbfReadOptions.LeaveOpen"/> does not apply.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>; its <see cref="DbcReadOptions.LeaveOpen"/> applies to every stream.</param>
    /// <returns>A reader positioned before the first record.</returns>
    public static DbfReader OpenDbcReader(IEnumerable<Stream> parts, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null)
    {
        var list = DbfReader.RequireParts(parts, nameof(parts));
        var leaveOpen = dbcOptions?.LeaveOpen ?? false;
        var inner = new DbcReadOptions { LeaveOpen = true, VerifyChecksum = dbcOptions?.VerifyChecksum ?? true };
        var prepared = new List<DbfPart>();
        try
        {
            for (var i = 0; i < list.Count; i++)
            {
                // The decompressing stream never disposes the caller's stream itself, so a failure
                // while opening leaves it untouched; the reader disposes it later unless LeaveOpen.
                var dbf = DbcFile.OpenAsDbf(list[i], inner);
                var name = list.Count == 1 ? null : $"part {i + 1}";
                prepared.Add(new DbfPart(name, dbf, leaveOpen ? [dbf] : [dbf, list[i]], [dbf], drainAtEnd: true));
            }
        }
        catch
        {
            DbfPart.DisposeCreated(prepared);
            throw;
        }

        return DbfReader.Create(prepared, dbfOptions);
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
        var extension = Path.GetExtension(fullPath);
        var match = PartName.Match(Path.GetFileNameWithoutExtension(fullPath));
        if (!match.Success)
        {
            throw new ArgumentException(
                $"'{Path.GetFileName(fullPath)}' does not follow the DATASUS naming (digits, optionally followed by a part letter).",
                nameof(path));
        }

        var baseName = match.Groups["base"].Value;
        var unsplit = new List<string>();
        var lettered = new SortedDictionary<char, List<string>>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = name.Substring(baseName.Length, name.Length - baseName.Length - extension.Length);
            if (rest.Length == 0)
            {
                unsplit.Add(file);
            }
            else if (rest.Length == 1 && rest[0] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                var letter = char.ToLowerInvariant(rest[0]);
                if (!lettered.TryGetValue(letter, out var files))
                {
                    lettered[letter] = files = [];
                }

                files.Add(file);
            }
        }

        var ambiguous = unsplit.Count > 1 ? unsplit : lettered.Values.FirstOrDefault(files => files.Count > 1);
        if (ambiguous is not null)
        {
            throw new IOException($"Ambiguous part names that differ only by case: {string.Join(", ", ambiguous.Select(Path.GetFileName))}.");
        }

        if (unsplit.Count == 1 && lettered.Count > 0)
        {
            throw new IOException(
                $"Both '{Path.GetFileName(unsplit[0])}' and lettered parts ({string.Join(", ", lettered.Values.Select(files => Path.GetFileName(files[0])))}) exist; remove the files that do not belong to the same version.");
        }

        if (lettered.Count == 0)
        {
            return unsplit.Count == 1
                ? [unsplit[0]]
                : throw new FileNotFoundException($"No file or parts found for '{baseName}{extension}'.", fullPath);
        }

        var expected = 'a';
        foreach (var letter in lettered.Keys)
        {
            if (letter != expected)
            {
                var missing = Path.Combine(directory, baseName + expected + extension);
                throw new FileNotFoundException($"Part '{expected}' of '{baseName}' is missing: '{Path.GetFileName(missing)}' was not found.", missing);
            }

            expected++;
        }

        return [.. lettered.Values.Select(files => files[0])];
    }

    private static DbfReader OpenPaths(List<string> paths, DbcReadOptions? dbcOptions, DbfReadOptions? dbfOptions, Func<string, bool> isDbc)
    {
        var prepared = new List<DbfPart>();
        try
        {
            foreach (var path in paths)
            {
                Stream stream = isDbc(path)
                    ? DbcFile.OpenAsDbf(path, dbcOptions)
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
}
