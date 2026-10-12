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
    /// It costs one directory listing per folder and no other request: the folder of each layout
    /// that the range reaches (two, for a range that starts in an older layout and ends in the one
    /// in use now) and, for SIM and SINASC from 1996 on, the folder of preliminary data. Some folders are large: the
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
    /// <para>
    /// The folder of preliminary data is optional: when the server answers 550 to its listing (FTP
    /// uses it both for a folder that does not exist and for one that may not be read), the result
    /// has the tables of the folder of final data only, and nothing tells that apart from a
    /// preliminary folder with no table in the range.
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
    /// <paramref name="uf"/> is not a state; a period is before
    /// <see cref="DatasusDataset.FirstPeriod"/>; or, for a dataset whose file names carry two
    /// digits of the year, a period is more than ninety-nine years after the start of the folder layout in use now (see
    /// <see cref="DatasusDataset.GetPath"/>).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A period is not set or is not of the dataset's periodicity, or <paramref name="from"/> is
    /// after <paramref name="to"/>.
    /// </exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of a folder of the dataset with a permanent reply (FTP 5xx).
    /// For the folder of final data that includes 550 (the folder does not exist or may not be
    /// read); for the folder of preliminary data, any reply but 550. It is not retried, and no
    /// partial result is returned.
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
                var (entries, _) = await ListPreliminaryAsync(era.PreliminaryDirectory, cancellationToken).ConfigureAwait(false);
                Collect(era, era.PreliminaryDirectory, isPreliminary: true, entries);
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
    /// when the layout of the period has a folder of preliminary data (SIM and SINASC, from 1996
    /// on), the preliminary one.
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
    /// a part before the last one is missing. When the server answered 550 to the listing of the
    /// folder of preliminary data, that folder was not searched: the message says so and has the
    /// reply, and the refusal is the <see cref="Exception.InnerException"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state; <paramref name="period"/> is before
    /// <see cref="DatasusDataset.FirstPeriod"/>; or, for a dataset whose file names carry two
    /// digits of the year, it is more than ninety-nine years after the start of the folder layout in use now (see
    /// <see cref="DatasusDataset.GetPath"/>).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="period"/> is not set or is not of the dataset's periodicity.</exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of a folder where the table was looked for, with a permanent
    /// reply (FTP 5xx). For the folder of final data that includes 550 (the folder does not exist
    /// or may not be read); for the folder of preliminary data, any reply but 550. It is not
    /// retried, and it is not reported as a table that does not exist.
    /// </exception>
    /// <exception cref="IOException">Ambiguous names on the server.</exception>
    public async Task<DatasusTableInfo> FindTableAsync(DatasusDataset dataset, DatasusUf uf, DatasusPeriod period, CancellationToken cancellationToken = default)
    {
        Guard.NotDisposed(_disposed, this);
        Guard.NotNull(dataset, nameof(dataset));
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = dataset.EraFor(period, nameof(period));
        var fileName = era.FileName(ufName, period);

        var table = await FindInAsync(dataset, era, era.Directory, fileName, uf, period, isPreliminary: false, cancellationToken).ConfigureAwait(false);
        DatasusRequestRefusedException? notListed = null;
        if (table is null && era.PreliminaryDirectory is not null)
        {
            (table, notListed) = await FindPreliminaryAsync(dataset, era, era.PreliminaryDirectory, fileName, uf, period, cancellationToken).ConfigureAwait(false);
        }

        return table ?? throw NoTable(dataset, era, ufName, period, fileName, notListed);
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
    /// preliminary one exists, and when the folder of preliminary data was not searched because
    /// the server answered 550 to its listing.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state; <paramref name="period"/> is before
    /// <see cref="DatasusDataset.FirstPeriod"/>; or, for a dataset whose file names carry two
    /// digits of the year, it is more than ninety-nine years after the start of the folder layout in use now (see
    /// <see cref="DatasusDataset.GetPath"/>).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="period"/> is not set or is not of the dataset's periodicity.</exception>
    /// <exception cref="DatasusRequestRefusedException">
    /// The server refused the listing of a folder where the table was looked for, with a permanent
    /// reply (FTP 5xx). For the folder of final data that includes 550 (the folder does not exist
    /// or may not be read); for the folder of preliminary data, any reply but 550. It is not
    /// retried, and it is not reported as a table that does not exist.
    /// </exception>
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

        DatasusRequestRefusedException? notListed = null;
        if (era.PreliminaryDirectory is not null)
        {
            (var preliminary, notListed) = await FindPreliminaryAsync(dataset, era, era.PreliminaryDirectory, fileName, uf, period, cancellationToken).ConfigureAwait(false);
            if (preliminary is not null)
            {
                throw new FileNotFoundException(
                    $"{dataset} of {ufName} for {period} is not published as final data: only the preliminary table {preliminary.Path} exists, and this method opens final data only. "
                    + "To read preliminary data, find the table with FindTableAsync and pass it to OpenReaderAsync(DatasusTableInfo).",
                    finalPath);
            }
        }

        throw NoTable(dataset, era, ufName, period, fileName, notListed);
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
        var remote = RemotePath.Parse(directory + "/" + fileName, Root);
        var parts = await FindPartsOrNullAsync(remote, nameof(fileName), cancellationToken).ConfigureAwait(false);
        return parts is null ? null : new DatasusTableInfo(dataset, uf, period, isPreliminary, TablePath(era, directory, parts[0].Name), parts);
    }

    // The preliminary candidate: the table there, or no table and, when the folder could not be
    // listed, the server's refusal, so that the caller can say that the folder was not searched.
    private async Task<(DatasusTableInfo? Table, DatasusRequestRefusedException? NotListed)> FindPreliminaryAsync(DatasusDataset dataset, DatasusEra era, string directory, string fileName, DatasusUf uf, DatasusPeriod period, CancellationToken cancellationToken)
    {
        var (_, notListed) = await ListPreliminaryAsync(directory, cancellationToken).ConfigureAwait(false);
        return notListed is null
            ? (await FindInAsync(dataset, era, directory, fileName, uf, period, isPreliminary: true, cancellationToken).ConfigureAwait(false), null)
            : (null, notListed);
    }

    // The folder of preliminary data is optional: a source may stop having one. A 550 to its
    // listing, and no other reply, is taken as "there is no such folder": no entries, and the
    // refusal itself, because FTP uses the same 550 for a folder that may not be read.
    private async Task<(IReadOnlyList<RemoteEntry> Entries, DatasusRequestRefusedException? NotListed)> ListPreliminaryAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return (await ListInternalAsync(RemotePath.Parse(directory, Root), cancellationToken).ConfigureAwait(false), null);
        }
        catch (DatasusRequestRefusedException exception) when (exception.FtpReplyCode == "550")
        {
            return ([], exception);
        }
    }

    private static FileNotFoundException NoTable(DatasusDataset dataset, DatasusEra era, string ufName, DatasusPeriod period, string fileName, DatasusRequestRefusedException? preliminaryNotListed)
    {
        var finalPath = era.Directory + "/" + fileName;
        if (preliminaryNotListed is not null)
        {
            // Not "final or preliminary": a folder that may not be read gets the same 550.
            return new FileNotFoundException(
                $"The server has no final {dataset} table of {ufName} for {period}: {finalPath} does not exist, whole or in parts. "
                + $"The folder of preliminary data, {era.PreliminaryDirectory}, was not searched, because it could not be listed: {preliminaryNotListed.Message}",
                finalPath,
                preliminaryNotListed);
        }

        return new FileNotFoundException(
            era.PreliminaryDirectory is null
                ? $"The server has no {dataset} table of {ufName} for {period}: {finalPath} does not exist, whole or in parts."
                : $"The server has no {dataset} table of {ufName} for {period}, final or preliminary: neither {finalPath} nor {era.PreliminaryDirectory}/{fileName} exists.",
            finalPath);
    }

    // The path of a table from the name of its first file, in the case the server uses: the name
    // itself, or the name without its part letter for a split table.
    private static string TablePath(DatasusEra era, string directory, string firstName) =>
        directory + "/" + (era.TryParseFileName(firstName, out _, out _, out var part) && part is not null
            ? DatasusEra.WithoutPartLetter(firstName)
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
