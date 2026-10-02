using System.Globalization;

namespace DbSus.Dbf;

/// <summary>
/// Parses the ASCII representations dBase uses for numbers, dates and logicals, without
/// allocating and independently of the current culture.
/// </summary>
internal static class DbfValueParser
{
    private const byte Space = (byte)' ';

    public static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == Space)
        {
            start++;
        }

        var end = value.Length;
        while (end > start && value[end - 1] == Space)
        {
            end--;
        }

        return value.Slice(start, end - start);
    }

    public static bool IsBlank(ReadOnlySpan<byte> value) => Trim(value).IsEmpty;

    /// <summary>Parses an <c>N</c>/<c>F</c> value; returns false when it is not a number.</summary>
    public static bool TryParseDecimal(ReadOnlySpan<byte> trimmed, out decimal value)
    {
        value = 0;
        var index = 0;
        var negative = false;
        if (index < trimmed.Length && (trimmed[index] == (byte)'-' || trimmed[index] == (byte)'+'))
        {
            negative = trimmed[index] == (byte)'-';
            index++;
        }

        ulong mantissa = 0;
        var digits = 0;
        var scale = -1;
        for (; index < trimmed.Length; index++)
        {
            var c = trimmed[index];
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                if (digits == 19)
                {
                    return TryParseWithFramework(trimmed, out value);
                }

                mantissa = (mantissa * 10) + (ulong)(c - '0');
                digits++;
                if (scale >= 0)
                {
                    scale++;
                }
            }
            else if (c == (byte)'.' && scale < 0)
            {
                scale = 0;
            }
            else
            {
                return false;
            }
        }

        if (digits == 0 || scale > 28)
        {
            return false;
        }

        value = new decimal((int)(mantissa & 0xFFFFFFFF), (int)(mantissa >> 32), 0, negative, (byte)Math.Max(scale, 0));
        return true;
    }

    /// <summary>
    /// Parses a date in the given layout: <c>yyyyMMdd</c> (as in <c>D</c> fields), <c>ddMMyyyy</c>,
    /// or <c>yyyyMM</c> (the first day of that month). Blank or all zeros is null.
    /// </summary>
    public static bool TryParseDate(ReadOnlySpan<byte> trimmed, DbfDateLayout layout, out DateTime? value)
    {
        value = null;
        if (trimmed.IsEmpty || IsAllZeros(trimmed))
        {
            return true;
        }

        int year, month, day = 1;
        switch (layout)
        {
            case DbfDateLayout.YearMonth:
                if (trimmed.Length != 6
                    || !TryParseDigits(trimmed.Slice(0, 4), out year)
                    || !TryParseDigits(trimmed.Slice(4, 2), out month))
                {
                    return false;
                }

                break;
            case DbfDateLayout.DayMonthYear:
                if (trimmed.Length != 8
                    || !TryParseDigits(trimmed.Slice(4, 4), out year)
                    || !TryParseDigits(trimmed.Slice(2, 2), out month)
                    || !TryParseDigits(trimmed.Slice(0, 2), out day))
                {
                    return false;
                }

                break;
            default:
                if (trimmed.Length != 8
                    || !TryParseDigits(trimmed.Slice(0, 4), out year)
                    || !TryParseDigits(trimmed.Slice(4, 2), out month)
                    || !TryParseDigits(trimmed.Slice(6, 2), out day))
                {
                    return false;
                }

                break;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        value = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return true;
    }

    /// <summary>Parses an <c>L</c> value. <c>?</c> or blank is null.</summary>
    public static bool TryParseBoolean(ReadOnlySpan<byte> trimmed, out bool? value)
    {
        value = null;
        if (trimmed.IsEmpty || (trimmed.Length == 1 && trimmed[0] == (byte)'?'))
        {
            return true;
        }

        if (trimmed.Length != 1)
        {
            return false;
        }

        switch ((char)trimmed[0])
        {
            case 'T' or 't' or 'Y' or 'y':
                value = true;
                return true;
            case 'F' or 'f' or 'N' or 'n':
                value = false;
                return true;
            default:
                return false;
        }
    }

    private static bool IsAllZeros(ReadOnlySpan<byte> value)
    {
        foreach (var c in value)
        {
            if (c != (byte)'0')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<byte> value, out int result)
    {
        result = 0;
        foreach (var c in value)
        {
            if (c is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            result = (result * 10) + (c - '0');
        }

        return true;
    }

    // More than 19 significant digits: rare enough to take the allocating path.
    private static bool TryParseWithFramework(ReadOnlySpan<byte> trimmed, out decimal value)
    {
        var chars = new char[trimmed.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)trimmed[i];
        }

        return decimal.TryParse(
            new string(chars),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);
    }
}
