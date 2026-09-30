namespace DbSus.Tests.TestSupport;

internal static class RepositoryPaths
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    public static string ExpectedHashes => Path.Combine(Root, "tests", "data", "expected.json");

    public static string LocalSamples => Path.Combine(Root, "tests", "data", "local");

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (DbSus.slnx) above the test output directory.");
    }
}
