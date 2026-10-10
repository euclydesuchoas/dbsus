using DbSus.Client.Transport;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Client;

// Finding and opening tables by dataset, state and period (see DatasusCatalog).
public sealed partial class DatasusClient
{
    /// <summary>
    /// Lists the tables of a dataset in a range of periods, from the server's directory listing:
    /// one entry per table, with the parts of a split table grouped together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It costs one directory listing per folder (the folder of the dataset and, for SIM and
    /// SINASC, the folder of preliminary data) and no other request. Some folders are large: the
    /// SIASUS one lists more than 50,000 files, whatever the range asked for. Listings are reused
    /// for <see cref="DatasusClientOptions.ListingCacheDuration"/> (10 minutes by default), so a
    /// table published in the meantime does not show until then.
    /// </para>
    /// <para>
    /// <b>A known limit:</b> the result is what the listing shows. A listing that arrives incomplete
    /// while the server still reports it as complete omits tables without any error, and so does
    /// one that is out of date. To know whether one table exists, use <see cref="FindTableAsync"/>,
    /// which asks the server for the file itself.
    /// </para>
    /// <para>
    /// Files that are not of the dataset are left out: other file types, files for the whole
    /// country, and names outside the pattern. A table is judged only when it is opened: a part
    /// missing in the middle shows here as the parts that are listed.
    /// </para>
    /// </remarks>
    /// <param name="dataset">The dataset, from <see cref="DatasusCatalog"/>.</param>
    /// <param name="from">The first period, inclusive: a month for a monthly dataset, a year for a yearly one.</param>
    /// <param name="to">The last period, inclusive.</param>
    /// <param name="uf">A state, or <see langword="null"/> for every state.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The tables listed, ordered by period and then by the abbreviation of the state. A table in the
    /// folder of preliminary data has <see cref="DatasusTableInfo.IsPreliminary"/> set; when a
    /// table is in both folders, the final one is returned.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state, or a period is before
    /// <see cref="DatasusDataset.FirstPeriod"/> (use paths as strings for older periods).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A period is not set or is not of the dataset's periodicity, or <paramref name="from"/> is
    /// after <paramref name="to"/>.
    /// </exception>
    public async Task<IReadOnlyList<DatasusTableInfo>> ListTablesAsync(DatasusDataset dataset, DatasusPeriod from, DatasusPeriod to, DatasusUf? uf = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        if (uf is { } state)
        {
            DatasusUfNames.Require(state, nameof(uf));
        }

        dataset.EraFor(from, nameof(from));
        dataset.EraFor(to, nameof(to));
        if (from > to)
        {
            throw new ArgumentException($"The range is empty: {from} is after {to}.", nameof(from));
        }

        var tables = new Dictionary<(DatasusPeriod Period, DatasusUf Uf), TableFiles>();
        foreach (var era in dataset.Eras)
        {
            if (era.FirstPeriod > to || (era.LastPeriod is { } last && last < from))
            {
                continue;
            }

            Collect(era, era.Directory, isPreliminary: false, await ListInternalAsync(RemotePath.Parse(era.Directory, Root), cancellationToken).ConfigureAwait(false));
            if (era.PreliminaryDirectory is not null)
            {
                Collect(era, era.PreliminaryDirectory, isPreliminary: true, await ListOrEmptyAsync(era.PreliminaryDirectory, cancellationToken).ConfigureAwait(false));
            }
        }

        var result = new List<DatasusTableInfo>(tables.Count);
        foreach (var pair in tables)
        {
            result.Add(pair.Value.ToTable(dataset, pair.Key.Uf, pair.Key.Period));
        }

        result.Sort(static (x, y) =>
        {
            var byPeriod = x.Period.CompareTo(y.Period);
            return byPeriod != 0 ? byPeriod : string.CompareOrdinal(DatasusUfNames.NameOf(x.Uf!.Value), DatasusUfNames.NameOf(y.Uf!.Value));
        });
        return result;

        void Collect(DatasusEra era, string directory, bool isPreliminary, IReadOnlyList<RemoteEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.IsDirectory
                    || !era.TryParseFileName(entry.Name, out var entryUf, out var period, out var part)
                    || period < from || period > to
                    || (uf is { } wanted && entryUf != wanted))
                {
                    continue;
                }

                if (!tables.TryGetValue((period, entryUf), out var files))
                {
                    tables[(period, entryUf)] = files = new TableFiles(era, directory, isPreliminary);
                }
                else if (files.IsPreliminary != isPreliminary)
                {
                    continue; // already found in the folder of final data, which comes first
                }

                files.Add(entry, part);
            }
        }
    }

    /// <summary>
    /// Finds the table of one state and period, asking the server: the final file first and then,
    /// for a source with a folder of preliminary data (SIM and SINASC), the preliminary one.
    /// </summary>
    /// <remarks>
    /// Each candidate is looked for as <see cref="FindPartsAsync"/> does: in the folder's listing,
    /// and then by asking the server for the file itself (and for the part after the last one
    /// listed), so an incomplete or out-of-date listing does not hide the table. This is the way to
    /// know whether a table exists and which files it has; pass the result to
    /// <see cref="OpenReaderAsync(DatasusTableInfo, DbfReadOptions, DbcReadOptions, CancellationToken)"/>
    /// to read it, or its parts to <see cref="DownloadAsync"/>.
    /// </remarks>
    /// <param name="dataset">The dataset, from <see cref="DatasusCatalog"/>.</param>
    /// <param name="uf">The state.</param>
    /// <param name="period">A month for a monthly dataset, a year for a yearly one.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// The table, with the exact size and time of each part, its path as the server lists it, and
    /// <see cref="DatasusTableInfo.IsPreliminary"/> telling in which folder it was found.
    /// </returns>
    /// <exception cref="FileNotFoundException">
    /// The server has no such table, final or preliminary (the message names the paths tried), or
    /// a part before the last one is missing.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state, or <paramref name="period"/> is before
    /// <see cref="DatasusDataset.FirstPeriod"/> (use the path as a string for older periods).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="period"/> is not set or is not of the dataset's periodicity.</exception>
    /// <exception cref="IOException">Ambiguous names on the server.</exception>
    public async Task<DatasusTableInfo> FindTableAsync(DatasusDataset dataset, DatasusUf uf, DatasusPeriod period, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = dataset.EraFor(period, nameof(period));
        var fileName = era.FileName(ufName, period);

        var table = await FindInAsync(dataset, era, era.Directory, fileName, uf, period, isPreliminary: false, cancellationToken).ConfigureAwait(false);
        if (table is null && era.PreliminaryDirectory is not null)
        {
            table = await FindInAsync(dataset, era, era.PreliminaryDirectory, fileName, uf, period, isPreliminary: true, cancellationToken).ConfigureAwait(false);
        }

        return table ?? throw new FileNotFoundException(
            era.PreliminaryDirectory is null
                ? $"The server has no {dataset} table of {ufName} for {period}: {era.Directory}/{fileName} does not exist, whole or in parts."
                : $"The server has no {dataset} table of {ufName} for {period}, final or preliminary: neither {era.Directory}/{fileName} nor {era.PreliminaryDirectory}/{fileName} exists.",
            era.Directory + "/" + fileName);
    }

    /// <summary>
    /// Finds the table of one state and period in the folder of <b>final</b> data, downloads its
    /// parts and opens them as one <see cref="DbfReader"/>, like
    /// <see cref="OpenReaderAsync(string, DbfReadOptions, DbcReadOptions, CancellationToken)"/>.
    /// </summary>
    /// <remarks>
    /// Preliminary data is never opened here. To read a year that SIM or SINASC still publish as
    /// preliminary, find the table with <see cref="FindTableAsync"/>, check
    /// <see cref="DatasusTableInfo.IsPreliminary"/>, and pass it to
    /// <see cref="OpenReaderAsync(DatasusTableInfo, DbfReadOptions, DbcReadOptions, CancellationToken)"/>.
    /// </remarks>
    /// <param name="dataset">The dataset, from <see cref="DatasusCatalog"/>.</param>
    /// <param name="uf">The state.</param>
    /// <param name="period">A month for a monthly dataset, a year for a yearly one.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <returns>A reader positioned before the first record, over every part in order.</returns>
    /// <exception cref="FileNotFoundException">
    /// The server has no final table for that state and period; the message says so when only a
    /// preliminary one exists.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state, or <paramref name="period"/> is before
    /// <see cref="DatasusDataset.FirstPeriod"/> (use the path as a string for older periods).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="period"/> is not set or is not of the dataset's periodicity.</exception>
    public async Task<DbfReader> OpenReaderAsync(DatasusDataset dataset, DatasusUf uf, DatasusPeriod period, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = dataset.EraFor(period, nameof(period));
        var fileName = era.FileName(ufName, period);
        var finalPath = era.Directory + "/" + fileName;

        var table = await FindInAsync(dataset, era, era.Directory, fileName, uf, period, isPreliminary: false, cancellationToken).ConfigureAwait(false);
        if (table is not null)
        {
            return await OpenPartsAsync(table.Parts, dbfOptions, dbcOptions, cancellationToken).ConfigureAwait(false);
        }

        if (era.PreliminaryDirectory is not null
            && await FindInAsync(dataset, era, era.PreliminaryDirectory, fileName, uf, period, isPreliminary: true, cancellationToken).ConfigureAwait(false) is { } preliminary)
        {
            throw new FileNotFoundException(
                $"{dataset} of {ufName} for {period} is not published as final data: only the preliminary table {preliminary.Path} exists, and this method opens final data only. "
                + "To read preliminary data, find the table with FindTableAsync and pass it to OpenReaderAsync(DatasusTableInfo).",
                finalPath);
        }

        throw new FileNotFoundException(
            era.PreliminaryDirectory is null
                ? $"The server has no {dataset} table of {ufName} for {period}: {finalPath} does not exist, whole or in parts."
                : $"The server has no {dataset} table of {ufName} for {period}, final or preliminary: neither {finalPath} nor {era.PreliminaryDirectory}/{fileName} exists.",
            finalPath);
    }

    /// <summary>
    /// Downloads the parts of a table already found, one after the other, and opens them as one
    /// <see cref="DbfReader"/>, without looking for the table again. This is how preliminary data
    /// is read: the table comes from <see cref="FindTableAsync"/> or <see cref="ListTablesAsync"/>,
    /// with <see cref="DatasusTableInfo.IsPreliminary"/> in plain sight.
    /// </summary>
    /// <remarks>
    /// The parts opened are exactly <see cref="DatasusTableInfo.Parts"/>. A table from
    /// <see cref="ListTablesAsync"/> has what the listing showed, which may be incomplete; one from
    /// <see cref="FindTableAsync"/> was confirmed with the server. As in every download, the
    /// server is asked for each part's size and time, so a file republished since is downloaded anew.
    /// </remarks>
    /// <param name="table">The table to open.</param>
    /// <param name="dbfOptions">DBF read options, or <see langword="null"/>.</param>
    /// <param name="dbcOptions">DBC read options, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the downloads.</param>
    /// <returns>A reader positioned before the first record, over every part in order.</returns>
    /// <exception cref="FileNotFoundException">A part is no longer on the server.</exception>
    public Task<DbfReader> OpenReaderAsync(DatasusTableInfo table, DbfReadOptions? dbfOptions = null, DbcReadOptions? dbcOptions = null, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(table, nameof(table));
        return OpenPartsAsync(table.Parts, dbfOptions, dbcOptions, cancellationToken);
    }

    // One candidate folder: the table there, confirmed with the server, or null when it has none.
    private async Task<DatasusTableInfo?> FindInAsync(DatasusDataset dataset, DatasusEra era, string directory, string fileName, DatasusUf uf, DatasusPeriod period, bool isPreliminary, CancellationToken cancellationToken)
    {
        if (isPreliminary && await ListOrNullAsync(directory, cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        var remote = RemotePath.Parse(directory + "/" + fileName, Root);
        var parts = await FindPartsOrNullAsync(remote, nameof(fileName), cancellationToken).ConfigureAwait(false);
        return parts is null ? null : new DatasusTableInfo(dataset, uf, period, isPreliminary, TablePath(era, directory, parts[0].Name), parts);
    }

    // The folder of preliminary data is optional: a source may stop having one.
    private async Task<IReadOnlyList<RemoteEntry>> ListOrEmptyAsync(string directory, CancellationToken cancellationToken) =>
        await ListOrNullAsync(directory, cancellationToken).ConfigureAwait(false) ?? [];

    private async Task<IReadOnlyList<RemoteEntry>?> ListOrNullAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await ListInternalAsync(RemotePath.Parse(directory, Root), cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    // The path of a table from the name of its first file, in the case the server uses: the name
    // itself, or the name without its part letter for a split table.
    private static string TablePath(DatasusEra era, string directory, string firstName) =>
        directory + "/" + (era.TryParseFileName(firstName, out _, out _, out var part) && part is not null
            ? firstName.Remove(era.Prefix.Length + 6, 1)
            : firstName);

    // The files of one table, as a listing shows them.
    private sealed class TableFiles(DatasusEra era, string directory, bool isPreliminary)
    {
        private readonly List<(char? Part, RemoteEntry Entry)> _files = [];

        public bool IsPreliminary => isPreliminary;

        public void Add(RemoteEntry entry, char? part) => _files.Add((part, entry));

        public DatasusTableInfo ToTable(DatasusDataset dataset, DatasusUf uf, DatasusPeriod period)
        {
            // A file without a part letter first, then the parts by letter.
            _files.Sort(static (x, y) => (x.Part ?? '\0').CompareTo(y.Part ?? '\0'));
            var parts = new DatasusFileInfo[_files.Count];
            for (var i = 0; i < parts.Length; i++)
            {
                var entry = _files[i].Entry;
                parts[i] = new DatasusFileInfo(directory + "/" + entry.Name, entry.Name, entry.Size, null, isDirectory: false);
            }

            return new DatasusTableInfo(dataset, uf, period, isPreliminary, TablePath(era, directory, parts[0].Name), parts);
        }
    }
}
