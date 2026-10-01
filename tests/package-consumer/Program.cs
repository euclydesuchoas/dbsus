// Smoke test of the DbSus and DbSus.Client packages as an outside application sees them. Exits
// with a non-zero code on the first failure. Uses only the committed CNES fixture; no network.
using System.Reflection;
using System.Runtime.Versioning;
using DbSus;
using DbSus.Client;
using DbSus.Dbc;
using DbSus.Dbf;

var root = FindRepositoryRoot();
var fixture = Path.Combine(root, "tests", "data", "fixtures", "LTRR0610.dbc");
var work = Path.Combine(Path.GetTempPath(), "dbsus-package-consumer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(work);

try
{
#if NETFRAMEWORK
    const string expectedAsset = ".NETFramework,Version=v4.7.2";
#elif NET10_0
    const string expectedAsset = ".NETCoreApp,Version=v10.0";
#else
    const string expectedAsset = ".NETStandard,Version=v2.0";
#endif
    Check("DbSus asset", AssetOf(typeof(DbfReader)) == expectedAsset, AssetOf(typeof(DbfReader)));
    Check("DbSus.Client asset", AssetOf(typeof(DatasusClient)) == expectedAsset, AssetOf(typeof(DatasusClient)));

    // Read with typed getters, the nullable generic getter and a conversion.
    var options = new DbfReadOptions();
    options.Conversions["CODUFMUN"] = DbfFieldConversion.Int32();
    long beds = 0;
    var records = 0;
    using (var reader = DbSusFile.OpenReader(fixture, options))
    {
        var count = reader.GetOrdinal("QT_EXIST");
        var municipality = reader.GetOrdinal("CODUFMUN");
        while (reader.Read())
        {
            records++;
            beds += reader.GetInt32(count);
            Check("nullable getter", reader.GetFieldValue<int?>(count) == reader.GetInt32(count), "mismatch");
            Check("conversion", reader.GetInt32(municipality) > 0, "not positive");
        }
    }

    Check("records", records == 102, records.ToString());
    Check("beds", beds == 652, beds.ToString());

    // DBC -> DBF conversion, then the plain DBF reader and a split-table lookup.
    var dbf = Path.Combine(work, "LTRR0610.dbf");
    DbcFile.ConvertToDbf(fixture, dbf);
    using (var reader = DbfReader.Open(dbf))
    {
        Check("converted DBF", reader.Header.RecordCount == 102, reader.Header.RecordCount.ToString());
    }

    File.Copy(fixture, Path.Combine(work, "LTRR0610a.dbc"));
    File.Copy(fixture, Path.Combine(work, "LTRR0610b.dbc"));
    using (var reader = DbSusFile.OpenDbcReader(DbSusFile.FindParts(Path.Combine(work, "LTRR0610.dbc"))))
    {
        var total = 0;
        while (reader.Read())
        {
            total++;
        }

        Check("split table", total == 204 && reader.PartCount == 2, total.ToString());
    }

    // The client and its FTP dependency load; no network call is made.
    using (var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = Path.Combine(work, "cache") }))
    {
        Check("FluentFTP", typeof(FluentFTP.AsyncFtpClient).Assembly.GetName().Name == "FluentFTP", "not loaded");
    }

    Console.WriteLine($"OK {expectedAsset} on {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}: {records} records, {beds} beds");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAILED: {exception}");
    return 1;
}
finally
{
    Directory.Delete(work, recursive: true);
}

static void Check(string what, bool condition, string detail)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{what}: {detail}");
    }
}

static string AssetOf(Type type) =>
    type.Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "unknown";

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Run inside the DbSus repository.");
}
