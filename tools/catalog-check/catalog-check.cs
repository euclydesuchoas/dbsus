// Compares DatasusCatalog with what the DATASUS server lists now: for each dataset, how many tables
// and states the listing shows and from when to when; for each folder, the names that no dataset
// of the catalog explains and that are not on the list of known exceptions below.
//
// USE IT SPARINGLY. It lists the catalog's folders on the real DATASUS server, some of them large
// (the SIASUS folder lists more than 50,000 files): 18 listings, one connection, one after the
// other, with a pause in between, no retries and no download. It is a manual check for when the
// layout may have changed. Never schedule it, and never run it in CI.
//
// Usage, from anywhere inside the repository:
//   dotnet run tools/catalog-check/catalog-check.cs            prints the folders it would list, and stops
//   dotnet run tools/catalog-check/catalog-check.cs -- --run   lists them on the server and reports
//
// It changes nothing: the report goes to the console, and updating the catalog or
// docs/datasus-paths.md after a change on the server is a decision for a person.
#:project ../../src/DbSus.Client/DbSus.Client.csproj

using System.Text.RegularExpressions;
using DbSus.Client;

// Names seen in the catalog's folders in October 2026 that are not files of the catalog (see
// "Outside the catalog" in docs/datasus-paths.md). Anything else that no dataset explains is reported.
Regex[] knownExceptions =
[
    new(@"^(AB|ABO|AMP|AN|SAD)[A-Z]{2}\d{4}\.dbc$", RegexOptions.IgnoreCase),   // other SIASUS file types
    new(@"^BI[A-Z]{2}\d{4}(_\d)?\.dbc$", RegexOptions.IgnoreCase),              // SIASUS-BI, with its _1, _2, _3
    new(@"^(CH|CM)BR\d{4}\.dbc$", RegexOptions.IgnoreCase),                     // SIHSUS files for the whole country
    new(@"^(DO|DN)(BR|EX)\d{4}\.dbc$", RegexOptions.IgnoreCase),                // SIM and SINASC, whole country and abroad
    new(@"^DNEX\d{2}\.dbc$", RegexOptions.IgnoreCase),
    new(@"^[A-Z]{2}ufAAmm\.dbc$"),                                              // placeholders in CNES folders
    new(@"\.(zip|dll)$", RegexOptions.IgnoreCase),
];

var pause = TimeSpan.FromSeconds(2);
var from = new Dictionary<DatasusDataset, DatasusPeriod>();
var folders = new SortedDictionary<string, List<DatasusDataset>>(StringComparer.Ordinal);
foreach (var dataset in DatasusCatalog.Datasets)
{
    from[dataset] = dataset.FirstPeriod;
    foreach (var candidate in dataset.GetCandidatePaths(DatasusUf.SP, dataset.FirstPeriod))
    {
        var folder = candidate.Remove(candidate.LastIndexOf('/'));
        if (!folders.TryGetValue(folder, out var datasets))
        {
            folders[folder] = datasets = [];
        }

        datasets.Add(dataset);
    }
}

if (!args.Contains("--run"))
{
    Console.WriteLine($"This would list {folders.Count} folders on the DATASUS server, one at a time:");
    foreach (var folder in folders)
    {
        Console.WriteLine($"  {folder.Key}   ({string.Join(", ", folder.Value)})");
    }

    Console.WriteLine("Some of them are large. Nothing was listed. To list them, run again with: -- --run");
    return 0;
}

await using var client = new DatasusClient(new DatasusClientOptions
{
    MaxConnections = 1,
    MaxRetries = 0,
    ListingCacheDuration = TimeSpan.FromHours(1),   // every folder is listed once, however many datasets it holds
});

// One listing per folder, kept for the datasets below.
var listed = new Dictionary<string, IReadOnlyList<DatasusFileInfo>>(StringComparer.Ordinal);
foreach (var folder in folders.Keys)
{
    if (listed.Count > 0)
    {
        await Task.Delay(pause);
    }

    try
    {
        listed[folder] = await client.ListAsync(folder);
        Console.WriteLine($"listed {folder}: {listed[folder].Count:N0} entries");
    }
    catch (IOException exception)
    {
        Console.WriteLine($"FAILED {folder}: {exception.GetType().Name}: {exception.Message}");
    }
}

Console.WriteLine();
var problems = 0;
var explained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (var dataset in DatasusCatalog.Datasets)
{
    var last = dataset.Periodicity == DatasusPeriodicity.Monthly ? DatasusPeriod.OfMonth(9999, 12) : DatasusPeriod.OfYear(9999);
    IReadOnlyList<DatasusTableInfo> tables;
    try
    {
        tables = await client.ListTablesAsync(dataset, from[dataset], last);   // from the listings already made
    }
    catch (IOException exception)
    {
        problems++;
        Console.WriteLine($"{dataset,-11} FAILED: {exception.Message}");
        continue;
    }

    foreach (var part in tables.SelectMany(table => table.Parts))
    {
        explained.Add(part.Path);
    }

    if (tables.Count == 0)
    {
        problems++;
        Console.WriteLine($"{dataset,-11} NO TABLE LISTED: the folder or the file names may have changed");
        continue;
    }

    var states = tables.Select(table => table.Uf).Distinct().Count();
    var preliminary = tables.Where(table => table.IsPreliminary).Select(table => table.Period).Distinct().ToList();
    Console.WriteLine(
        $"{dataset,-11} {tables.Count,6:N0} tables, {states,2} states, {tables.Min(table => table.Period)} to {tables.Max(table => table.Period)}, {tables.Count(table => table.Parts.Count > 1):N0} split"
        + (preliminary.Count == 0 ? string.Empty : $", preliminary: {string.Join(", ", preliminary)}"));
}

Console.WriteLine();
foreach (var folder in listed)
{
    var unexplained = folder.Value
        .Where(entry => !entry.IsDirectory && !explained.Contains(entry.Path) && !knownExceptions.Any(pattern => pattern.IsMatch(entry.Name)))
        .Select(entry => entry.Name)
        .OrderBy(name => name, StringComparer.Ordinal)
        .ToList();
    var directories = folder.Value.Where(entry => entry.IsDirectory).Select(entry => entry.Name).ToList();
    if (unexplained.Count == 0 && directories.Count == 0)
    {
        continue;
    }

    problems++;
    Console.WriteLine($"{folder.Key}: {unexplained.Count:N0} file(s) that the catalog does not explain, {directories.Count} subfolder(s)");
    foreach (var name in unexplained.Take(20).Concat(directories.Select(name => name + "/")))
    {
        Console.WriteLine($"  {name}");
    }

    if (unexplained.Count > 20)
    {
        Console.WriteLine($"  ... and {unexplained.Count - 20:N0} more");
    }
}

Console.WriteLine(problems == 0
    ? "The listings agree with the catalog."
    : $"{problems} point(s) to look at, above. Nothing was changed.");
return problems == 0 ? 0 : 1;
