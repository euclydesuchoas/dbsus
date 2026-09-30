using BenchmarkDotNet.Attributes;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Benchmarks;

/// <summary>
/// End-to-end read costs on a real, local-only DATASUS file (never committed; see
/// tests/data/local). Baseline for deciding what, if anything, to optimise.
/// </summary>
[MemoryDiagnoser]
public class ReadBenchmarks
{
    private string _path = string.Empty;

    /// <summary>File name inside tests/data/local.</summary>
    [Params("RDMG2407.dbc")]
    public string File { get; set; } = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(RepositoryRoot(), "tests", "data", "local", File);
        if (!System.IO.File.Exists(_path))
        {
            throw new FileNotFoundException($"Benchmark input {File} is missing from tests/data/local.", _path);
        }
    }

    /// <summary>Decompression only: DBC to DBF bytes, discarded.</summary>
    [Benchmark(Baseline = true)]
    public void Decompress()
    {
        using var dbf = DbcFile.OpenAsDbf(_path);
        dbf.CopyTo(Stream.Null);
    }

    /// <summary>Decompression plus record framing, without parsing any value.</summary>
    [Benchmark]
    public long ReadRecords()
    {
        using var reader = DbSusFile.OpenDbcReader(_path);
        long count = 0;
        while (reader.Read())
        {
            count++;
        }

        return count;
    }

    /// <summary>Every value of every field, boxed, as DataTable.Load or SqlBulkCopy would read them.</summary>
    [Benchmark]
    public long ReadAllValues()
    {
        using var reader = DbSusFile.OpenDbcReader(_path);
        var values = new object[reader.FieldCount];
        long count = 0;
        while (reader.Read())
        {
            count += reader.GetValues(values);
        }

        return count;
    }

    /// <summary>Two typed fields through a projection, the typical analytical query.</summary>
    [Benchmark]
    public decimal ReadTwoProjectedFields()
    {
        using var reader = DbSusFile.OpenDbcReader(_path, new DbfReadOptions { Columns = ["VAL_TOT", "MUNIC_RES"] });
        decimal total = 0;
        while (reader.Read())
        {
            total += reader.GetDecimal(0);
            _ = reader.GetString(1);
        }

        return total;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (System.IO.File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the benchmarks from inside the DbSus repository.");
    }
}
