using System.Globalization;

namespace DbSus.Client;

/// <summary>
/// A range of periods in which the files of a <see cref="DatasusDataset"/> keep the same folder
/// and the same file name pattern. DATASUS has changed both over time (for example, SIA files up
/// to 2007 are in another folder, and SIM files up to 1995 have another prefix and a two-digit
/// year), so a file type is described by a list of eras.
/// </summary>
/// <remarks>
/// An era names the file of a state (<see cref="StatePrefix"/>), the file for the whole country
/// (<see cref="NationalStem"/>), or both.
/// </remarks>
internal sealed class DatasusEra
{
    private const string Extension = ".dbc";

    /// <summary>Creates an era.</summary>
    /// <param name="firstPeriod">The first period of the era.</param>
    /// <param name="lastPeriod">The last period of the era, or <see langword="null"/> for the current one.</param>
    /// <param name="directory">The folder of the files, relative to the client's root.</param>
    /// <param name="preliminaryDirectory">The folder of preliminary data, or <see langword="null"/> when the source has none.</param>
    /// <param name="statePrefix">The letters before the state in the name of the file of a state, or <see langword="null"/> when the era has no such file.</param>
    /// <param name="nationalStem">The letters before the period in the name of the file for the whole country, or <see langword="null"/> when the era has no such file.</param>
    /// <param name="yearDigits">The digits of the year in a file name: 2 or 4 for a yearly file, always 2 for a monthly one.</param>
    public DatasusEra(DatasusPeriod firstPeriod, DatasusPeriod? lastPeriod, string directory, string? preliminaryDirectory, string? statePrefix, string? nationalStem, int yearDigits)
    {
        if (statePrefix is null && nationalStem is null)
        {
            throw new ArgumentException("An era names the file of a state, the file for the whole country, or both.", nameof(statePrefix));
        }

        if (yearDigits is not (2 or 4) || (yearDigits == 4 && firstPeriod.Periodicity == DatasusPeriodicity.Monthly))
        {
            throw new ArgumentOutOfRangeException(nameof(yearDigits), yearDigits, "The year has two or four digits in a yearly file name, and two in a monthly one.");
        }

        if (lastPeriod is { } last && (last.Periodicity != firstPeriod.Periodicity || last < firstPeriod))
        {
            throw new ArgumentException("An era ends in a period of the same kind as its first one, and not before it.", nameof(lastPeriod));
        }

        FirstPeriod = firstPeriod;
        LastPeriod = lastPeriod;
        Directory = directory;
        PreliminaryDirectory = preliminaryDirectory;
        StatePrefix = statePrefix;
        NationalStem = nationalStem;
        YearDigits = yearDigits;
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

    /// <summary>
    /// Gets the letters that start the name of the file of a state, e.g. <c>PA</c>, or
    /// <see langword="null"/> when the era has no file per state.
    /// </summary>
    public string? StatePrefix { get; }

    /// <summary>
    /// Gets the letters that start the name of the file for the whole country, e.g. <c>DOBR</c> or
    /// <c>DOEXT</c>, or <see langword="null"/> when the era has no such file.
    /// </summary>
    public string? NationalStem { get; }

    /// <summary>Gets the digits of the year in a file name: 2 or 4.</summary>
    public int YearDigits { get; }

    /// <summary>Gets whether the files are monthly or yearly.</summary>
    public DatasusPeriodicity Periodicity => FirstPeriod.Periodicity;

    /// <summary>
    /// Gets the file name pattern of the era: that of the file of a state when there is one, e.g.
    /// <c>PA{UF}{YY}{MM}.dbc</c> or <c>DO{UF}{YYYY}.dbc</c>, and otherwise that of the file for
    /// the whole country.
    /// </summary>
    public string FileNamePattern => StateFileNamePattern ?? NationalFileNamePattern!;

    /// <summary>Gets the name pattern of the file of a state, or <see langword="null"/> when the era has none.</summary>
    public string? StateFileNamePattern => StatePrefix is null ? null : StatePrefix + "{UF}" + PeriodPattern + Extension;

    /// <summary>Gets the name pattern of the file for the whole country, e.g. <c>DOBR{YYYY}.dbc</c>, or <see langword="null"/> when the era has none.</summary>
    public string? NationalFileNamePattern => NationalStem is null ? null : NationalStem + PeriodPattern + Extension;

    /// <summary>
    /// Gets the last year a file name of the era can tell apart, or <see langword="null"/> when
    /// the name carries the four digits of the year.
    /// </summary>
    public int? LastNameableYear => YearDigits == 2 ? FirstPeriod.Year + 99 : null;

    private string PeriodPattern => Periodicity == DatasusPeriodicity.Monthly ? "{YY}{MM}" : YearDigits == 2 ? "{YY}" : "{YYYY}";

    // The digits of the period in a file name: the year and the month with two each, or the year alone.
    private int PeriodDigits => Periodicity == DatasusPeriodicity.Monthly ? 4 : YearDigits;

    /// <summary>
    /// Returns a file name without its part letter, which is the character before the extension.
    /// </summary>
    public static string WithoutPartLetter(string fileName) => fileName.Remove(fileName.Length - Extension.Length - 1, 1);

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
    /// <exception cref="InvalidOperationException">The era has no file per state.</exception>
    public string FileName(string ufName, DatasusPeriod period) =>
        (StatePrefix ?? throw new InvalidOperationException("The era has no file per state.")) + ufName + PeriodText(period) + Extension;

    /// <summary>
    /// Builds the name of the file for the whole country of one period: upper-case letters and a
    /// lower-case extension. The server may list the name in another case.
    /// </summary>
    /// <exception cref="InvalidOperationException">The era has no file for the whole country.</exception>
    public string NationalFileName(DatasusPeriod period) =>
        (NationalStem ?? throw new InvalidOperationException("The era has no file for the whole country.")) + PeriodText(period) + Extension;

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
        if (StatePrefix is not { } prefix)
        {
            return false;
        }

        // Prefix, two letters, the digits of the period, an optional letter, the extension.
        var length = fileName.Length - prefix.Length - Extension.Length;
        var withoutPart = 2 + PeriodDigits;
        if ((length != withoutPart && length != withoutPart + 1)
            || !fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            || !DatasusUfNames.TryParse(fileName, prefix.Length, out uf)
            || !TryReadPeriod(fileName, prefix.Length + 2, out period))
        {
            return false;
        }

        if (length == withoutPart + 1)
        {
            var letter = fileName[prefix.Length + withoutPart];
            if (letter is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z')))
            {
                return false;
            }

            part = char.ToLowerInvariant(letter);
        }

        return true;
    }

    /// <summary>
    /// Reads the period from the name of a file for the whole country of this era, ignoring case.
    /// The name must be exactly the stem, the digits of the period and the extension.
    /// </summary>
    /// <param name="fileName">A file name, without a folder.</param>
    /// <param name="period">The period.</param>
    public bool TryParseNationalFileName(string fileName, out DatasusPeriod period)
    {
        period = default;
        return NationalStem is { } stem
            && fileName.Length == stem.Length + PeriodDigits + Extension.Length
            && fileName.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
            && TryReadPeriod(fileName, stem.Length, out period);
    }

    private string PeriodText(DatasusPeriod period) => Periodicity == DatasusPeriodicity.Monthly
        ? (period.Year % 100).ToString("D2", CultureInfo.InvariantCulture) + period.Month!.Value.ToString("D2", CultureInfo.InvariantCulture)
        : YearDigits == 2
            ? (period.Year % 100).ToString("D2", CultureInfo.InvariantCulture)
            : period.Year.ToString("D4", CultureInfo.InvariantCulture);

    // Reads the digits of a period at a position of a file name; the period must belong to the era.
    private bool TryReadPeriod(string fileName, int start, out DatasusPeriod period)
    {
        period = default;
        var digits = 0;
        for (var i = start; i < start + PeriodDigits; i++)
        {
            if (fileName[i] is < '0' or > '9')
            {
                return false;
            }

            digits = (digits * 10) + (fileName[i] - '0');
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
        else if (YearDigits == 2)
        {
            period = DatasusPeriod.OfYear(YearNamedBy(digits));
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
