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
/// This version builds paths only in the folder layout that DATASUS uses now, from
/// <see cref="FirstPeriod"/> on. Earlier periods are in older layouts and are reached with the path
/// as a string.
/// </para>
/// </remarks>
public sealed class DatasusDataset
{
    private const string PathsGuide = "https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md";

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
    /// This version builds paths only for the files of a state.
    /// </summary>
    public DatasusCoverage Coverage { get; }

    /// <summary>
    /// Gets the first period this version accepts: the start of the folder layout that DATASUS
    /// uses now. It is not the first period with data: a file type may have started later.
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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state, or <paramref name="period"/> is before
    /// <see cref="FirstPeriod"/> (use the path as a string for older periods).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public string GetPath(DatasusUf uf, DatasusPeriod period)
    {
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
    /// <returns>One path, or two for a source with a folder of preliminary data; the first is <see cref="GetPath"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="uf"/> is not a state, or <paramref name="period"/> is before
    /// <see cref="FirstPeriod"/> (use the path as a string for older periods).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="period"/> is not set, is a year where a month is needed, or is a month where
    /// a year is needed.
    /// </exception>
    public IReadOnlyList<string> GetCandidatePaths(DatasusUf uf, DatasusPeriod period)
    {
        var ufName = DatasusUfNames.Require(uf, nameof(uf));
        var era = EraFor(period, nameof(period));
        var fileName = era.FileName(ufName, period);
        return era.PreliminaryDirectory is null
            ? [era.Directory + "/" + fileName]
            : [era.Directory + "/" + fileName, era.PreliminaryDirectory + "/" + fileName];
    }

    /// <summary>Returns the source and the code, e.g. <c>SIHSUS-RD</c>.</summary>
    public override string ToString() => Source + "-" + Code;

    internal static DatasusDataset Monthly(string source, string code, string officialName, string directory, int firstYear, int firstMonth) =>
        new(source, code, officialName, DatasusCoverage.Uf, new DatasusEra(DatasusPeriod.OfMonth(firstYear, firstMonth), directory, null, code));

    internal static DatasusDataset Yearly(string source, string code, string officialName, DatasusCoverage coverage, string directory, string? preliminaryDirectory, int firstYear) =>
        new(source, code, officialName, coverage, new DatasusEra(DatasusPeriod.OfYear(firstYear), directory, preliminaryDirectory, code));

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
                return era;
            }
        }

        throw new ArgumentOutOfRangeException(
            paramName,
            period,
            $"{this} files before {FirstPeriod} are in an older folder layout, which this version does not build. Use the path as a string instead; the layouts are described in {PathsGuide}");
    }
}
