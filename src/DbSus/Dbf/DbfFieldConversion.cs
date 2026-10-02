using System.Diagnostics.CodeAnalysis;

namespace DbSus.Dbf;

/// <summary>
/// An opt-in conversion of a character (<c>C</c>) field into a typed value, declared per field in
/// <see cref="DbfReadOptions.Conversions"/>. DATASUS stores many numbers and dates as text, and the
/// date layout differs between sources, so nothing is converted unless asked.
/// </summary>
/// <remarks>
/// A converted field reports the target type through <c>GetFieldType</c>, the schema and
/// <c>GetValue</c>; its text is still available through <c>GetRawBytes</c>. Blank values are null;
/// dates made only of zeros are null. Values that do not parse, have a fraction where a whole
/// number is expected, or do not fit the target type throw <see cref="FormatException"/> naming
/// the field and record, never the value.
/// </remarks>
[SuppressMessage("Naming", "CA1720", Justification = "The factory names deliberately mirror the target .NET type (Int32, Int64, Decimal).")]
public sealed class DbfFieldConversion
{
    private const string YearMonthDay = "yyyyMMdd";
    private const string DayMonthYear = "ddMMyyyy";
    private const string YearMonth = "yyyyMM";

    private DbfFieldConversion(DbfValueKind kind, Type targetType, string? format)
    {
        Kind = kind;
        TargetType = targetType;
        Format = format;
    }

    /// <summary>Gets the .NET type the field is converted to.</summary>
    public Type TargetType { get; }

    /// <summary>Gets the date format for date conversions; otherwise <see langword="null"/>.</summary>
    public string? Format { get; }

    internal DbfValueKind Kind { get; }

    internal DbfDateLayout DateLayout => Format switch
    {
        DayMonthYear => DbfDateLayout.DayMonthYear,
        YearMonth => DbfDateLayout.YearMonth,
        _ => DbfDateLayout.YearMonthDay,
    };

    /// <summary>Converts digits (with optional sign and surrounding spaces) to <see cref="int"/>.</summary>
    public static DbfFieldConversion Int32() => new(DbfValueKind.Int32, typeof(int), null);

    /// <summary>Converts digits (with optional sign and surrounding spaces) to <see cref="long"/>.</summary>
    public static DbfFieldConversion Int64() => new(DbfValueKind.Int64, typeof(long), null);

    /// <summary>Converts a number with an optional <c>.</c> decimal point to <see cref="decimal"/>.</summary>
    public static DbfFieldConversion Decimal() => new(DbfValueKind.Decimal, typeof(decimal), null);

    /// <summary>
    /// Converts a date stored as digits to <see cref="DateTime"/>. Supported formats:
    /// <c>"yyyyMMdd"</c> (SIH, SIA, CNES), <c>"ddMMyyyy"</c> (SIM, SINASC), and <c>"yyyyMM"</c>, a
    /// year and month (such as a processing month), returned as the first day of that month.
    /// </summary>
    /// <remarks>
    /// The value, without surrounding spaces, must have exactly as many digits as the format.
    /// A conversion applies to one field, so a year and a month stored in two separate fields
    /// cannot be combined into one date this way.
    /// </remarks>
    /// <exception cref="ArgumentException">The format is not one of the supported ones.</exception>
    public static DbfFieldConversion Date(string format)
    {
        Guard.NotNull(format, nameof(format));
        if (format != YearMonthDay && format != DayMonthYear && format != YearMonth)
        {
            throw new ArgumentException(
                $"Unsupported date format '{format}'; use \"{YearMonthDay}\", \"{DayMonthYear}\" or \"{YearMonth}\".", nameof(format));
        }

        return new DbfFieldConversion(DbfValueKind.Date, typeof(DateTime), format);
    }

    /// <inheritdoc/>
    public override string ToString() => Format is null ? TargetType.Name : $"{TargetType.Name} ({Format})";
}
