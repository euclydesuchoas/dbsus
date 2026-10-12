namespace DbSus.Client;

/// <summary>
/// One kind of file that DATASUS publishes, such as SIHSUS-RD or CNES-LT: where its files are and
/// how they are named. The known ones are in <see cref="DatasusCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// A dataset builds paths; it never reaches the server. It checks only the shape of a request
/// (a month for a monthly file type, a year for a yearly one, a real state), never whether the
/// file exists: DATASUS publishes late, leaves gaps and does not have every state in every
/// period. A path built here may therefore name a file that is not on the server.
/// </para>
/// <para>
/// DATASUS moved some file types to other folders over time, some with other file names. The
/// catalog knows the folder layout in use now and, for six file types, the one before it; a path
/// is built in the layout its period belongs to, from <see cref="FirstPeriod"/> on.
/// </para>
/// </remarks>
public sealed class DatasusDataset
{
    internal const string PathsGuide = "https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md";

    internal DatasusDataset(string source, string code, string officialName, DatasusCoverage coverage, params DatasusEra[] eras)
    {
        Source = source;
        Code = code;
        OfficialName = officialName;
        Coverage = coverage;
        Eras = eras;
    }

    /// <summary>Gets the source (the DATASUS information system), e.g. <c>SIHSUS</c>.</summary>
    public string Source { get; }

    /// <summary>Gets the code of the file type within its source, e.g. <c>RD</c>. File names start with it.</summary>
    public string Code { get; }

    /// <summary>
    /// Gets the name under which DATASUS publishes this file type, in Portuguese and spelled as
    /// DATASUS spells it, e.g. <c>AIH Reduzida</c>.
    /// </summary>
    public string OfficialName { get; }

    /// <summary>Gets whether the files are monthly or yearly.</summary>
    public DatasusPeriodicity Periodicity => CurrentEra.Periodicity;

    /// <summary>
    /// Gets the areas the files cover: one file per state, one for the whole country, or both.
    /// The path of the file of a state comes from <see cref="GetPath"/>, and that of the file for
    /// the whole country from <see cref="GetNationalPath"/>.
    /// </summary>
    public DatasusCoverage Coverage { get; }

    /// <summary>
    /// Gets the first period the catalog accepts: the start of the oldest folder layout it knows
    /// for this file type. It is not the first period with data: a file type may have started later.
    /// </summary>
    public DatasusPeriod FirstPeriod => Eras[0].FirstPeriod;

    /// <summary>Gets the eras, oldest first; the last one is the current era.</summary>
    internal IReadOnlyList<DatasusEra> Eras { get; }

    /// <summary>Gets the era of the folder layout that DATASUS uses now.</summary>
    internal DatasusEra CurrentEra => Eras[Eras.Count - 1];

    /// <summary>
    /// Builds the path of the file of one state and period, relative to
    /// <see cref="DatasusClientOptions.RootPath"/>, e.g. <c>/SIHSUS/200801_/Dados/RDSP2407.dbc</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder and the name are those of the layout the period belongs to: for SIM-DO,
    /// <c>/SIM/CID9/DORES/DORSP95.dbc</c> for 1995 and <c>/SIM/CID10/DORES/DOSP1996.dbc</c> for 1996.
    /// </para>
    /// <para>
    /// The path names the file without a part letter. For a table split into parts (SIA-PA in the
    /// larger states), pass it to <see cref="DatasusClient.FindPartsAsync"/> or to
    /// <c>DatasusClient.OpenReaderAsync</c>, which find the parts.
    /// </para>
    /// <para>
    /// The path is where the final file is published. For the most recent years of SIM and SINASC,
    /// the file may still be in the folder of preliminary data instead: use
    /// <see cref="DatasusClient.FindTableAsync"/>, which looks in both and tells which one it found.
    /// </para>
    /// <para>
    /// The name is built in upper case with a lower-case extension. The server lists some names
    /// in another case and, as observed in October 2026, accepts either; the name as the server
    /// lists it is the one in <see cref="DatasusTableInfo.Path"/>, from
    /// <see cref="DatasusClient.FindTableAsync"/>.
    /// </para>
    /// </remarks>
    /// <param name="uf">The state.</param>
    /// <param name="period">A month for a monthly file type, a year for a yearly one.</param>
    /// <returns>The path, starting with <c>/</c>. The file may not exist on the server.</returns>
    /// <exception cref="NotSupportedException">
    /// The file type has one file for the whole country and none by state (see
    /// <see cref="Coverage"/>): use <see cref="GetNationalPath"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state; <paramref name="period"/> is before
    /// <see cref="FirstPeriod"/>; or, for a file type whose file names carry two digits of the
    /// year (every monthly one, and PCE and RESP), it is more than ninety-nine years after the
    /// start of the folder layout in use now: the name would be the one of an earlier period.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public string GetPath(DatasusUf uf, DatasusPeriod period)
    {
        RequireFilesByState();
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = EraFor(period, nameof(period));
        return era.Directory + "/" + era.FileName(ufName, period);
    }

    /// <summary>
    /// Builds every path where the file of one state and period may be, in the order to try them:
    /// the final file first and then, for a source that publishes preliminary data in a folder of
    /// its own (SIM and SINASC), the preliminary file.
    /// </summary>
    /// <remarks>
    /// Which years are still preliminary changes as DATASUS closes them, so it is not known here.
    /// <see cref="DatasusClient.FindTableAsync"/> asks the server for each candidate, in this order,
    /// and returns the first that exists.
    /// </remarks>
    /// <param name="uf">The state.</param>
    /// <param name="period">A month for a monthly file type, a year for a yearly one.</param>
    /// <returns>
    /// One path, or two when the layout of the period has a folder of preliminary data (SIM and
    /// SINASC, from 1996 on); the first is <see cref="GetPath"/>.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The file type has one file for the whole country and none by state (see
    /// <see cref="Coverage"/>): use <see cref="GetNationalCandidatePaths"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state; <paramref name="period"/> is before
    /// <see cref="FirstPeriod"/>; or, for a file type whose file names carry two digits of the
    /// year (every monthly one, and PCE and RESP), it is more than ninety-nine years after the
    /// start of the folder layout in use now: the name would be the one of an earlier period.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public IReadOnlyList<string> GetCandidatePaths(DatasusUf uf, DatasusPeriod period)
    {
        RequireFilesByState();
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = EraFor(period, nameof(period));
        var fileName = era.FileName(ufName, period);
        return era.PreliminaryDirectory is null
            ? [era.Directory + "/" + fileName]
            : [era.Directory + "/" + fileName, era.PreliminaryDirectory + "/" + fileName];
    }

    /// <summary>
    /// Builds the path of the file for the whole country of one period, relative to
    /// <see cref="DatasusClientOptions.RootPath"/>, e.g. <c>/SIM/CID10/DORES/DOBR2011.dbc</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The folder and the name are those of the layout the period belongs to: for SIM-DO,
    /// <c>/SIM/CID9/DORES/DORBR95.dbc</c> for 1995 and <c>/SIM/CID10/DORES/DOBR1996.dbc</c> for 1996.
    /// </para>
    /// <para>
    /// The path is where the final file is published. For the most recent years of SIM and SINASC,
    /// the file may still be in the folder of preliminary data instead: use
    /// <see cref="DatasusClient.FindNationalTableAsync"/>, which looks in both and tells which one
    /// it found.
    /// </para>
    /// <para>
    /// The name is built in upper case with a lower-case extension, as in <see cref="GetPath"/>.
    /// </para>
    /// </remarks>
    /// <param name="period">A month for a monthly file type, a year for a yearly one.</param>
    /// <returns>The path, starting with <c>/</c>. The file may not exist on the server.</returns>
    /// <exception cref="NotSupportedException">
    /// The file type has one file per state and none for the whole country (see
    /// <see cref="Coverage"/>): use <see cref="GetPath"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="period"/> is before <see cref="FirstPeriod"/>; or, for a file type whose
    /// file names carry two digits of the year, it is more than ninety-nine years after the start
    /// of the folder layout in use now: the name would be the one of an earlier period.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public string GetNationalPath(DatasusPeriod period)
    {
        RequireFileForTheCountry();
        var era = EraFor(period, nameof(period));
        return era.Directory + "/" + era.NationalFileName(period);
    }

    /// <summary>
    /// Builds every path where the file for the whole country of one period may be, in the order to
    /// try them: the final file first and then, when the layout of the period has a folder of
    /// preliminary data, the preliminary file.
    /// </summary>
    /// <remarks>
    /// Which years are still preliminary changes as DATASUS closes them, so it is not known here.
    /// <see cref="DatasusClient.FindNationalTableAsync"/> asks the server for each candidate, in
    /// this order, and returns the first that exists.
    /// </remarks>
    /// <param name="period">A month for a monthly file type, a year for a yearly one.</param>
    /// <returns>
    /// One path, or two when the layout of the period has a folder of preliminary data; the first
    /// is <see cref="GetNationalPath"/>.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// The file type has one file per state and none for the whole country (see
    /// <see cref="Coverage"/>): use <see cref="GetCandidatePaths"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="period"/> is before <see cref="FirstPeriod"/>; or, for a file type whose
    /// file names carry two digits of the year, it is more than ninety-nine years after the start
    /// of the folder layout in use now: the name would be the one of an earlier period.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public IReadOnlyList<string> GetNationalCandidatePaths(DatasusPeriod period)
    {
        RequireFileForTheCountry();
        var era = EraFor(period, nameof(period));
        var fileName = era.NationalFileName(period);
        return era.PreliminaryDirectory is null
            ? [era.Directory + "/" + fileName]
            : [era.Directory + "/" + fileName, era.PreliminaryDirectory + "/" + fileName];
    }

    /// <summary>Returns the source and the code, e.g. <c>SIHSUS-RD</c>.</summary>
    public override string ToString() => Source + "-" + Code;

    internal static DatasusDataset Monthly(string source, string code, string officialName, string directory, int firstYear, int firstMonth) =>
        new(source, code, officialName, DatasusCoverage.Uf, new DatasusEra(DatasusPeriod.OfMonth(firstYear, firstMonth), null, directory, null, code, null, 2));

    // The file for the whole country, when the file type has one, has BR in place of the state.
    internal static DatasusDataset Yearly(string source, string code, string officialName, DatasusCoverage coverage, string directory, string? preliminaryDirectory, int firstYear, int yearDigits = 4) =>
        new(source, code, officialName, coverage, new DatasusEra(DatasusPeriod.OfYear(firstYear), null, directory, preliminaryDirectory, code, NationalStem(coverage, code), yearDigits));

    // A monthly file type with an older folder layout: the same file name in another folder, up to
    // the month before the layout in use now starts.
    internal static DatasusDataset Monthly(string source, string code, string officialName, string directory, int firstYear, int firstMonth, string olderDirectory, int olderFirstYear, int olderFirstMonth)
    {
        var first = DatasusPeriod.OfMonth(firstYear, firstMonth);
        var lastOfTheOlder = firstMonth == 1 ? DatasusPeriod.OfMonth(firstYear - 1, 12) : DatasusPeriod.OfMonth(firstYear, firstMonth - 1);
        return new(
            source,
            code,
            officialName,
            DatasusCoverage.Uf,
            new DatasusEra(DatasusPeriod.OfMonth(olderFirstYear, olderFirstMonth), lastOfTheOlder, olderDirectory, null, code, null, 2),
            new DatasusEra(first, null, directory, null, code, null, 2));
    }

    // A yearly file type with an older folder layout, which has another folder, another prefix
    // and its own digits of the year, and ends the year before the layout in use now starts. The
    // file for the whole country of the older layout has a name of its own too.
    internal static DatasusDataset Yearly(
        string source, string code, string officialName, DatasusCoverage coverage, string directory, string? preliminaryDirectory, int firstYear, string olderDirectory, string olderPrefix, int olderYearDigits, int olderFirstYear, string? olderNationalStem) =>
        new(
            source,
            code,
            officialName,
            coverage,
            new DatasusEra(DatasusPeriod.OfYear(olderFirstYear), DatasusPeriod.OfYear(firstYear - 1), olderDirectory, null, olderPrefix, olderNationalStem, olderYearDigits),
            new DatasusEra(DatasusPeriod.OfYear(firstYear), null, directory, preliminaryDirectory, code, NationalStem(coverage, code), 4));

    private static string? NationalStem(DatasusCoverage coverage, string code) => coverage.HasFlag(DatasusCoverage.National) ? code + "BR" : null;

    /// <summary>Gets a value indicating whether the file type has one file per state.</summary>
    internal bool HasFilesByState => Coverage.HasFlag(DatasusCoverage.Uf);

    /// <summary>Gets a value indicating whether the file type has a file for the whole country.</summary>
    internal bool HasFileForTheCountry => Coverage.HasFlag(DatasusCoverage.National);

    // What is said when the files asked for are of an area the file type does not have: the names
    // of the methods that reach the files it has.
    internal string NoFilesByState(string methods) => $"{this} has one file for the whole country and none by state: use {methods}.";

    internal string NoFileForTheCountry(string methods) => $"{this} has one file per state and none for the whole country: use {methods}.";

    private void RequireFilesByState()
    {
        if (!HasFilesByState)
        {
            throw new NotSupportedException(NoFilesByState("GetNationalPath or GetNationalCandidatePaths"));
        }
    }

    private void RequireFileForTheCountry()
    {
        if (!HasFileForTheCountry)
        {
            throw new NotSupportedException(NoFileForTheCountry("GetPath or GetCandidatePaths"));
        }
    }

    // Checks the shape of the period and finds its era. Availability is never checked.
    internal DatasusEra EraFor(DatasusPeriod period, string paramName)
    {
        if (period.IsDefault)
        {
            throw new ArgumentException("The period is not set: use DatasusPeriod.OfMonth or DatasusPeriod.OfYear.", paramName);
        }

        if (period.Periodicity != Periodicity)
        {
            throw new ArgumentException(
                Periodicity == DatasusPeriodicity.Monthly
                    ? $"{this} is published monthly: the period needs a month, e.g. DatasusPeriod.OfMonth({period.Year}, 1)."
                    : $"{this} is published yearly: the period must be a whole year, e.g. DatasusPeriod.OfYear({period.Year}).",
                paramName);
        }

        foreach (var era in Eras)
        {
            if (era.Contains(period))
            {
                // Beyond the hundred years that two digits can name, the file name would be the
                // one of an earlier period, and the wrong table would be found without any error.
                if (era.LastNameableYear is { } lastYear && period.Year > lastYear)
                {
                    var namesakeYear = era.YearNamedBy(period.Year % 100);
                    var namesake = period.Month is { } month ? DatasusPeriod.OfMonth(namesakeYear, month) : DatasusPeriod.OfYear(namesakeYear);
                    throw new ArgumentOutOfRangeException(
                        paramName,
                        period,
                        $"{this} file names carry two digits of the year, so the catalog builds them only up to {lastYear}: {period} would get the name of {namesake}.");
                }

                return era;
            }
        }

        throw new ArgumentOutOfRangeException(
            paramName,
            period,
            $"{this} has no files before {FirstPeriod} in the folder layouts the catalog knows. See {PathsGuide}");
    }
}
