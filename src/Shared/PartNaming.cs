using System.Text.RegularExpressions;

namespace DbSus;

/// <summary>
/// The DATASUS naming rules for tables split into parts (<c>PAMG2407a.dbc</c>, <c>PAMG2407b.dbc</c>,
/// ...). Compiled into both DbSus (local folders) and DbSus.Client (FTP listings) so that both apply
/// exactly the same rules.
/// </summary>
internal static class PartNaming
{
    private static readonly Regex NamePattern = new(@"^(?<base>.*\d)(?<part>[A-Za-z])?$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits a file name into its base name (without part letter) and extension. The name must end
    /// in digits, optionally followed by one part letter, before the extension.
    /// </summary>
    /// <exception cref="ArgumentException">The name does not follow the DATASUS naming.</exception>
    public static (string BaseName, string Extension) Parse(string fileName, string paramName)
    {
        var extension = Path.GetExtension(fileName);
        var match = NamePattern.Match(Path.GetFileNameWithoutExtension(fileName));
        if (!match.Success)
        {
            throw new ArgumentException(
                $"'{fileName}' does not follow the DATASUS naming (digits, optionally followed by a part letter).",
                paramName);
        }

        return (match.Groups["base"].Value, extension);
    }

    /// <summary>
    /// Selects, among the names of the files in one folder, the parts of <paramref name="baseName"/>
    /// with <paramref name="extension"/>, ordered by part letter; or the single unsplit file.
    /// Names are compared ignoring case.
    /// </summary>
    /// <param name="candidates">File names (not paths) present in the folder.</param>
    /// <param name="baseName">Base name, without part letter.</param>
    /// <param name="extension">Extension, including the dot.</param>
    /// <param name="missing">Builds the exception for a missing file: (expected file name, message).</param>
    /// <exception cref="IOException">Names differ only by case, or unsplit and lettered files coexist.</exception>
    public static List<string> Select(IEnumerable<string> candidates, string baseName, string extension, Func<string, string, Exception> missing)
    {
        var unsplit = new List<string>();
        var lettered = new SortedDictionary<char, List<string>>();
        foreach (var name in candidates)
        {
            if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                || !name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)
                || name.Length < baseName.Length + extension.Length)
            {
                continue;
            }

            var rest = name.Substring(baseName.Length, name.Length - baseName.Length - extension.Length);
            if (rest.Length == 0)
            {
                unsplit.Add(name);
            }
            else if (rest.Length == 1 && rest[0] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                var letter = char.ToLowerInvariant(rest[0]);
                if (!lettered.TryGetValue(letter, out var files))
                {
                    lettered[letter] = files = [];
                }

                files.Add(name);
            }
        }

        var ambiguous = unsplit.Count > 1 ? unsplit : lettered.Values.FirstOrDefault(files => files.Count > 1);
        if (ambiguous is not null)
        {
            throw new IOException($"Ambiguous part names that differ only by case: {string.Join(", ", ambiguous)}.");
        }

        if (unsplit.Count == 1 && lettered.Count > 0)
        {
            throw new IOException(
                $"Both '{unsplit[0]}' and lettered parts ({string.Join(", ", lettered.Values.Select(files => files[0]))}) exist; remove the files that do not belong to the same version.");
        }

        if (lettered.Count == 0)
        {
            return unsplit.Count == 1
                ? [unsplit[0]]
                : throw missing(baseName + extension, $"No file or parts found for '{baseName}{extension}'.");
        }

        var expected = 'a';
        foreach (var letter in lettered.Keys)
        {
            if (letter != expected)
            {
                var name = baseName + expected + extension;
                throw missing(name, $"Part '{expected}' of '{baseName}' is missing: '{name}' was not found.");
            }

            expected++;
        }

        return [.. lettered.Values.Select(files => files[0])];
    }
}
