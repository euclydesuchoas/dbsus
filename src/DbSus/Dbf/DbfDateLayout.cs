namespace DbSus.Dbf;

/// <summary>How the digits of a date are laid out.</summary>
internal enum DbfDateLayout
{
    /// <summary><c>yyyyMMdd</c>, as in <c>D</c> fields.</summary>
    YearMonthDay,

    /// <summary><c>ddMMyyyy</c>.</summary>
    DayMonthYear,

    /// <summary><c>yyyyMM</c>: the first day of that month.</summary>
    YearMonth,
}
