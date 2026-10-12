using System.Globalization;

namespace DbSus.Client;

/// <summary>
/// A range of periods in which the files of a <see cref="DatasusDataset"/> keep the same folder
/// and the same file name pattern. DATASUS has changed both over time (for example, SIA files up
/// to 2007 are in another folder, and SIM files up to 1995 have another prefix and a two-digit
/// year), so a file type is described by a list of eras.
/// </summary>
internal sealed class DatasusEra
{
    private const string Extension = ".dbc";

    public DatasusEra(DatasusPeriod firstPeriod, string directory, string? preliminaryDirectory, string prefix)
    {
        FirstPeriod = firstPeriod;
        Directory = directory;
        PreliminaryDirectory = preliminaryDirectory;
        Prefix = prefix;
    }

    /// <summary>Gets the first period of the era.</summary>
    public DatasusPeriod FirstPeriod { get; }

    /// <summary>Gets the last period of the era, or <see langword="null"/> for the current one.</summary>
    public DatasusPeriod? LastPeriod { get; }

    /// <summary>Gets the folder of the files, relative to the client's root, e.g. <c>/SIASUS/200801_/Dados</c>.</summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the folder where DATASUS publishes the most recent years while they are preliminary,
    /// e.g. <c>/SIM/PRELIM/DORES</c>, or <see langword="null"/> when the source has none.
    /// </summary>
    public string? PreliminaryDirectory { get; }

    /// <summary>Gets the letters that start every file name, e.g. <c>PA</c>.</summary>
    public string Prefix { get; }

    /// <summary>Gets whether the files are monthly or yearly.</summary>
    public DatasusPeriodicity Periodicity => FirstPeriod.Periodicity;

    /// <summary>Gets the file name pattern, e.g. <c>PA{UF}{YY}{MM}.dbc</c> or <c>DO{UF}{YYYY}.dbc</c>.</summary>
    public string FileNamePattern => Prefix + (Periodicity == DatasusPeriodicity.Monthly ? "{UF}{YY}{MM}" : "{UF}{YYYY}") + Extension;

    /// <summary>
    /// Gets the last year a file name of the era can tell apart, or <see langword="null"/> when
    /// the name carries the four digits of the year. A monthly name carries two.
    /// </summary>
    public int? LastNameableYear => Periodicity == DatasusPeriodicity.Monthly ? FirstPeriod.Year + 99 : null;

    /// <summary>Whether the period belongs to the era.</summary>
    public bool Contains(DatasusPeriod period) => period >= FirstPeriod && (LastPeriod is not { } last || period <= last);

    /// <summary>
    /// Gets the year that two digits name in a file name of the era: the one within the hundred
    /// years that start with the era.
    /// </summary>
    public int YearNamedBy(int twoDigits)
    {
        var year = FirstPeriod.Year - (FirstPeriod.Year % 100) + twoDigits;
        return year < FirstPeriod.Year ? year + 100 : year;
    }

    /// <summary>
    /// Builds the name of the file of one state and period, without a part letter: upper-case
    /// letters and a lower-case extension. The server may list the name in another case.
    /// </summary>
    public string FileName(string ufName, DatasusPeriod period) => Periodicity == DatasusPeriodicity.Monthly
        ? Prefix + ufName + (period.Year % 100).ToString("D2", CultureInfo.InvariantCulture) + period.Month!.Value.ToString("D2", CultureInfo.InvariantCulture) + Extension
        : Prefix + ufName + period.Year.ToString("D4", CultureInfo.InvariantCulture) + Extension;

    /// <summary>
    /// Reads the state, the period and the part letter from a file name of this era, ignoring case.
    /// The name must be exactly the prefix, the abbreviation of a state, the digits of the period,
    /// at most one part letter, and the extension; anything else is not a file of this era.
    /// </summary>
    /// <param name="fileName">A file name, without a folder.</param>
    /// <param name="uf">The state.</param>
    /// <param name="period">The period.</param>
    /// <param name="part">The part letter in lower case, or <see langword="null"/> for a file that is not a part.</param>
    public bool TryParseFileName(string fileName, out DatasusUf uf, out DatasusPeriod period, out char? part)
    {
        uf = default;
        period = default;
        part = null;

        // Prefix, two letters, four digits, an optional letter, the extension.
        var length = fileName.Length - Prefix.Length - Extension.Length;
        if (length is not (6 or 7)
            || !fileName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || !DatasusUfNames.TryParse(fileName, Prefix.Length, out uf))
        {
            return false;
        }

        var digits = 0;
        for (var i = Prefix.Length + 2; i < Prefix.Length + 6; i++)
        {
            if (fileName[i] is < '0' or > '9')
            {
                return false;
            }

            digits = (digits * 10) + (fileName[i] - '0');
        }

        if (length == 7)
        {
            var letter = fileName[Prefix.Length + 6];
            if (letter is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
            {
                return false;
            }

            part = char.ToLowerInvariant(letter);
        }

        if (Periodicity == DatasusPeriodicity.Monthly)
        {
            var month = digits % 100;
            if (month is < 1 or > 12)
            {
                return false;
            }

            period = DatasusPeriod.OfMonth(YearNamedBy(digits / 100), month);
        }
        else
        {
            if (digits == 0)
            {
                return false;
            }

            period = DatasusPeriod.OfYear(digits);
        }

        return Contains(period);
    }
}
