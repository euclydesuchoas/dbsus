using System.Globalization;

namespace DbSus.Client;

/// <summary>How often DATASUS publishes the files of one file type.</summary>
public enum DatasusPeriodicity
{
    /// <summary>One file per month.</summary>
    Monthly = 0,

    /// <summary>One file per year.</summary>
    Yearly = 1,
}

/// <summary>The areas that the files of one file type cover.</summary>
[Flags]
public enum DatasusCoverage
{
    /// <summary>No area: the value of <c>default(DatasusCoverage)</c>. No file type of the catalog has it.</summary>
    None = 0,

    /// <summary>One file per state (see <see cref="DatasusUf"/>).</summary>
    Uf = 1,

    /// <summary>
    /// One file for the whole country. Its path comes from <see cref="DatasusDataset.GetNationalPath"/>,
    /// and the client reaches it with the methods that have National in their names.
    /// </summary>
    National = 2,
}

/// <summary>
/// The period a DATASUS file covers: a month (<see cref="OfMonth"/>) or a whole year
/// (<see cref="OfYear"/>), according to the <see cref="DatasusPeriodicity"/> of its file type.
/// </summary>
/// <remarks>
/// <para>
/// A year and a month are different periods: <c>OfYear(2024)</c> is never equal to
/// <c>OfMonth(2024, 1)</c>. Periods are ordered by year and then by month, and a year comes before
/// each of its own months: <c>OfYear(2024) &lt; OfMonth(2024, 1) &lt; OfMonth(2024, 12) &lt; OfYear(2025)</c>.
/// </para>
/// <para>
/// <c>default(DatasusPeriod)</c> is not a period: its <see cref="Year"/> is zero, and the methods
/// that take a period reject it with an <see cref="ArgumentException"/>.
/// </para>
/// </remarks>
public readonly struct DatasusPeriod : IEquatable<DatasusPeriod>, IComparable<DatasusPeriod>
{
    private readonly int _month;

    private DatasusPeriod(int year, int month)
    {
        Year = year;
        _month = month;
    }

    /// <summary>Gets the year (zero only for <c>default(DatasusPeriod)</c>).</summary>
    public int Year { get; }

    /// <summary>Gets the month, 1 to 12, or <see langword="null"/> for a whole year.</summary>
    public int? Month => _month == 0 ? null : _month;

    /// <summary>Gets whether the period is a month or a whole year.</summary>
    public DatasusPeriodicity Periodicity => _month == 0 ? DatasusPeriodicity.Yearly : DatasusPeriodicity.Monthly;

    internal bool IsDefault => Year == 0;

    /// <summary>Creates the period of one month.</summary>
    /// <param name="year">The year, 1 to 9999.</param>
    /// <param name="month">The month, 1 to 12.</param>
    /// <exception cref="ArgumentOutOfRangeException">The year or the month is out of range.</exception>
    public static DatasusPeriod OfMonth(int year, int month)
    {
        CheckYear(year);
        if (month is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "The month must be between 1 and 12.");
        }

        return new DatasusPeriod(year, month);
    }

    /// <summary>Creates the period of one whole year.</summary>
    /// <param name="year">The year, 1 to 9999.</param>
    /// <exception cref="ArgumentOutOfRangeException">The year is out of range.</exception>
    public static DatasusPeriod OfYear(int year)
    {
        CheckYear(year);
        return new DatasusPeriod(year, 0);
    }

    /// <summary>Indicates whether two periods are the same.</summary>
    public static bool operator ==(DatasusPeriod left, DatasusPeriod right) => left.Equals(right);

    /// <summary>Indicates whether two periods differ.</summary>
    public static bool operator !=(DatasusPeriod left, DatasusPeriod right) => !left.Equals(right);

    /// <summary>Indicates whether <paramref name="left"/> comes before <paramref name="right"/>.</summary>
    public static bool operator <(DatasusPeriod left, DatasusPeriod right) => left.CompareTo(right) < 0;

    /// <summary>Indicates whether <paramref name="left"/> comes before <paramref name="right"/> or is the same.</summary>
    public static bool operator <=(DatasusPeriod left, DatasusPeriod right) => left.CompareTo(right) <= 0;

    /// <summary>Indicates whether <paramref name="left"/> comes after <paramref name="right"/>.</summary>
    public static bool operator >(DatasusPeriod left, DatasusPeriod right) => left.CompareTo(right) > 0;

    /// <summary>Indicates whether <paramref name="left"/> comes after <paramref name="right"/> or is the same.</summary>
    public static bool operator >=(DatasusPeriod left, DatasusPeriod right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Indicates whether the other period is the same: the same year and, for months, the same
    /// month. A year is never equal to a month.
    /// </summary>
    public bool Equals(DatasusPeriod other) => Year == other.Year && _month == other._month;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is DatasusPeriod other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Year * 16) + _month;

    /// <summary>
    /// Compares by year and then by month; a year comes before each of its own months.
    /// </summary>
    /// <returns>A negative number, zero or a positive number, as this period comes before, is the same as, or comes after the other.</returns>
    public int CompareTo(DatasusPeriod other) => Year != other.Year ? Year.CompareTo(other.Year) : _month.CompareTo(other._month);

    /// <summary>Returns <c>2024-07</c> for a month and <c>2024</c> for a year.</summary>
    public override string ToString() => _month == 0
        ? Year.ToString("D4", CultureInfo.InvariantCulture)
        : Year.ToString("D4", CultureInfo.InvariantCulture) + "-" + _month.ToString("D2", CultureInfo.InvariantCulture);

    private static void CheckYear(int year)
    {
        if (year is < 1 or > 9999)
        {
            throw new ArgumentOutOfRangeException(nameof(year), year, "The year must be between 1 and 9999.");
        }
    }
}
