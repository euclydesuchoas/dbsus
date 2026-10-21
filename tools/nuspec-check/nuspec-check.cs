// Checks the versions in packed nuspecs against the rules nuget.org applies when a package is
// uploaded, so that a package it would refuse fails here instead: the version of the package, and
// the minimum and the maximum of every dependency of every target framework group.
//
// Usage, from anywhere inside the repository:
//   dotnet run tools/nuspec-check/nuspec-check.cs -- artifacts/packages
// Each argument is a .nupkg, a .snupkg, a .nuspec, or a folder whose files of those kinds are all
// checked (not its subfolders). The process fails, naming the file, the group and the dependency,
// when any version breaks a rule, or when no file was found.
//
// The rules come from NuGetGallery (https://github.com/NuGet/NuGetGallery):
// src/NuGetGallery.Core/Packaging/ManifestValidator.cs, which rejects a dependency version with
// metadata ("+..."), a floating range ("1.*"), and a version that is not SemVer 2.0 (a single release
// label and no metadata) and fails IsValidVersionForLegacyClients, in
// src/NuGetGallery.Core/NuGetVersionExtensions.cs: the pattern below, with its single release label
// starting with a letter, for the NuGet 2.x clients. So "0.2.0-0" is refused, and "0.2.0-a" and
// "0.2.0-preview.1" (two identifiers, SemVer 2.0) are accepted. Metadata is refused in the version of
// the package too, which nuget.org itself would accept.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

var legacyPattern = new Regex(@"^(?<Version>\d+(\s*\.\s*\d+){0,3})(?<Release>-[a-z][0-9a-z-]*)?$",
    RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
string[] kinds = [".nupkg", ".snupkg", ".nuspec"];

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run tools/nuspec-check/nuspec-check.cs -- <.nupkg, .snupkg, .nuspec or folder>...");
    return 2;
}

var files = new List<string>();
var errors = new List<string>();
foreach (var argument in args)
{
    if (Directory.Exists(argument))
    {
        files.AddRange(Directory.GetFiles(argument).Where(path => kinds.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).Order(StringComparer.Ordinal));
    }
    else if (File.Exists(argument) && kinds.Contains(Path.GetExtension(argument), StringComparer.OrdinalIgnoreCase))
    {
        files.Add(argument);
    }
    else
    {
        errors.Add($"{argument}: not a .nupkg, .snupkg, .nuspec or folder.");
    }
}

if (files.Count == 0)
{
    errors.Add("No .nupkg, .snupkg or .nuspec file found.");
}

foreach (var file in files)
{
    var name = Path.GetFileName(file);
    var nuspec = ReadNuspec(file, errors);
    if (nuspec is null)
    {
        continue;
    }

    var metadata = nuspec.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
    var version = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "version")?.Value.Trim();
    if (string.IsNullOrEmpty(version))
    {
        errors.Add($"{name}: the nuspec has no version.");
        continue;
    }

    var before = errors.Count;
    CheckVersion(version, $"{name}: the version of the package, {version},", errors);

    var checkedVersions = 0;
    var dependencies = metadata!.Elements().FirstOrDefault(element => element.Name.LocalName == "dependencies");
    foreach (var dependency in dependencies?.Descendants().Where(element => element.Name.LocalName == "dependency") ?? [])
    {
        var group = dependency.Parent?.Name.LocalName == "group"
            ? $"group {dependency.Parent.Attribute("targetFramework")?.Value ?? "(any)"}"
            : "no group";
        var id = dependency.Attribute("id")?.Value ?? "(no id)";
        var range = dependency.Attribute("version")?.Value ?? "";
        var place = $"{name}: {group}: dependency {id} {range}";
        if (range.Contains('*'))
        {
            errors.Add($"{place}: a floating range is refused by nuget.org.");
            continue;
        }

        var (minimum, maximum) = Bounds(range.Trim(), place, errors);
        if (minimum is not null)
        {
            CheckVersion(minimum, $"{place}: the lower bound {minimum}", errors);
            checkedVersions++;
        }

        if (maximum is not null)
        {
            CheckVersion(maximum, $"{place}: the upper bound {maximum}", errors);
            checkedVersions++;
        }
    }

    var groups = dependencies?.Elements().Count(element => element.Name.LocalName == "group") ?? 0;
    if (errors.Count == before)
    {
        Console.WriteLine($"ok: {name}: version {version}, {groups} dependency groups, {checkedVersions} dependency versions checked.");
    }
}

if (errors.Count > 0)
{
    foreach (var error in errors)
    {
        Console.Error.WriteLine("error: " + error);
    }

    return 1;
}

return 0;

static XDocument? ReadNuspec(string file, List<string> errors)
{
    try
    {
        if (Path.GetExtension(file).Equals(".nuspec", StringComparison.OrdinalIgnoreCase))
        {
            return XDocument.Load(file);
        }

        using var archive = ZipFile.OpenRead(file);
        var entries = archive.Entries.Where(entry => !entry.FullName.Contains('/') && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1)
        {
            errors.Add($"{Path.GetFileName(file)}: {entries.Length} nuspec files at the root of the package; one was expected.");
            return null;
        }

        using var stream = entries[0].Open();
        return XDocument.Load(stream);
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or System.Xml.XmlException or UnauthorizedAccessException)
    {
        errors.Add($"{Path.GetFileName(file)}: could not be read ({exception.Message}).");
        return null;
    }
}

// The bounds of a NuGet version range: "1.0" (a minimum), "[1.0]" (exactly), "[1.0, 2.0)",
// "(, 2.0]" and so on; an empty bound is null.
static (string? Minimum, string? Maximum) Bounds(string range, string place, List<string> errors)
{
    if (range.Length == 0)
    {
        errors.Add($"{place}: the version is empty.");
        return (null, null);
    }

    if (range[0] is not ('[' or '('))
    {
        return (range, null);
    }

    if (range[^1] is not (']' or ')'))
    {
        errors.Add($"{place}: the range is not closed.");
        return (null, null);
    }

    var parts = range[1..^1].Split(',');
    if (parts.Length == 1 && range[0] == '[' && range[^1] == ']')
    {
        return (parts[0].Trim(), null);
    }

    if (parts.Length != 2)
    {
        errors.Add($"{place}: the range is not of the form [minimum, maximum).");
        return (null, null);
    }

    var minimum = parts[0].Trim();
    var maximum = parts[1].Trim();
    return (minimum.Length == 0 ? null : minimum, maximum.Length == 0 || maximum == minimum ? null : maximum);
}

void CheckVersion(string version, string what, List<string> errors)
{
    if (version.Contains('+'))
    {
        errors.Add($"{what} has metadata (\"+...\"); nuget.org refuses it in a dependency version, and so does this check in the version of the package.");
        return;
    }

    var dash = version.IndexOf('-');
    var semVer2 = dash >= 0 && version[(dash + 1)..].Contains('.');
    if (!semVer2 && !legacyPattern.IsMatch(version.Trim()))
    {
        errors.Add($"{what} has a single release label that does not start with a letter (or is not a version); nuget.org refuses it with \"The package manifest contains an invalid Version: '{version}'\".");
    }
}
