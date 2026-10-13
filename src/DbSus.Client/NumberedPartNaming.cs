using System.Globalization;

namespace DbSus.Client;

/// <summary>
/// The names of a table split into numbered parts (<c>BIMG2312_1.dbc</c>, <c>BIMG2312_2.dbc</c>,
/// ...), as SIASUS-BI has them on the DATASUS server. It is a rule of the catalog of the client:
/// <see cref="DbSusFile.FindParts(string)"/> and <see cref="DatasusClient.FindPartsAsync"/> know
/// lettered parts only.
/// </summary>
internal static class NumberedPartNaming
{
    /// <summary>
    /// Selects, among the names of the files in one folder, the numbered parts of
    /// <paramref name="baseName"/> with <paramref name="extension"/>, in the order of their
    /// numbers, and tells whether the folder has the file without a number. Names are compared
    /// ignoring case.
    /// </summary>
    /// <param name="candidates">File names (not paths) present in the folder.</param>
    /// <param name="baseName">Base name, without a part number.</param>
    /// <param name="extension">Extension, including the dot.</param>
    /// <param name="unsplit">The name of the file without a number, as listed, or <see langword="null"/>.</param>
    /// <param name="missing">Builds the exception for a missing part: (expected file name, message).</param>
    /// <returns>The numbered parts, from 1 on with none missing; empty when the folder has none.</returns>
    /// <exception cref="IOException">Names differ only by case.</exception>
    public static List<string> Select(IEnumerable<string> candidates, string baseName, string extension, out string? unsplit, Func<string, string, Exception> missing)
    {
        var numbered = new SortedDictionary<int, string>();
        unsplit = null;
        foreach (var name in candidates)
        {
            if (string.Equals(name, baseName + extension, StringComparison.OrdinalIgnoreCase))
            {
                unsplit = unsplit is null ? name : throw new IOException($"Ambiguous part names that differ only by case: {unsplit}, {name}.");
            }
            else if (TryReadNumber(name, baseName, extension, out var number))
            {
                if (numbered.TryGetValue(number, out var other))
                {
                    throw new IOException($"Ambiguous part names that differ only by case: {other}, {name}.");
                }

                numbered[number] = name;
            }
        }

        var names = new List<string>(numbered.Count);
        foreach (var pair in numbered)
        {
            var expected = names.Count + 1;
            if (pair.Key != expected)
            {
                var name = Name(baseName, expected, extension);
                throw missing(name, $"Part {expected.ToString(CultureInfo.InvariantCulture)} of '{baseName}' is missing: '{name}' was not found.");
            }

            names.Add(pair.Value);
        }

        return names;
    }

    /// <summary>Builds the name of a numbered part, e.g. <c>BIMG2312_2.dbc</c>.</summary>
    public static string Name(string baseName, int number, string extension) => baseName + "_" + number.ToString(CultureInfo.InvariantCulture) + extension;

    /// <summary>
    /// Reads the number of a part from its name: the base name, an underscore, a number from 1 on
    /// with no zero in front and at most nine digits, and the extension, ignoring case.
    /// </summary>
    public static bool TryReadNumber(string name, string baseName, string extension, out int number)
    {
        number = 0;
        var digits = name.Length - baseName.Length - extension.Length - 1;
        if (digits is < 1 or > 9
            || !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            || name[baseName.Length] != '_'
            || name[baseName.Length + 1] is not (>= '1' and <= '9'))
        {
            return false;
        }

        for (var index = baseName.Length + 1; index < baseName.Length + 1 + digits; index++)
        {
            if (name[index] is not (>= '0' and <= '9'))
            {
                number = 0;
                return false;
            }

            number = (number * 10) + (name[index] - '0');
        }

        return true;
    }
}
