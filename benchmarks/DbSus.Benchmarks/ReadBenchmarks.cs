using BenchmarkDotNet.Attributes;
using DbSus.Dbc;
using DbSus.Dbf;

namespace DbSus.Benchmarks;

/// <summary>
/// End-to-end read costs on a large, local-only DATASUS file (never committed; see
/// tests/data/local): SIA-PA MG 2024-07 part b, 126 MB compressed, 1,173,720,147 bytes of DBF,
/// 2,956,469 records of 60 fields. Baseline for deciding what, if anything, to optimize.
/// Throughput = DBF bytes / mean time; allocation per record = allocated / records.
/// </summary>
[MemoryDiagnoser]
public class ReadBenchmarks
{
    private string _path = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _path = Path.Combine(RepositoryRoot(), "tests", "data", "local", "PAMG2407b.dbc");
        if (!File.Exists(_path))
        {
            throw new FileNotFoundException("Benchmark input PAMG2407b.dbc is missing from tests/data/local.", _path);
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

    /// <summary>Typed getters on native numeric fields, ordinals looked up before the loop.</summary>
    [Benchmark]
    public decimal TypedGetters()
    {
        using var reader = DbSusFile.OpenDbcReader(_path);
        var produced = reader.GetOrdinal("PA_QTDPRO");
        var approved = reader.GetOrdinal("PA_QTDAPR");
        var value = reader.GetOrdinal("PA_VALAPR");
        decimal total = 0;
        while (reader.Read())
        {
            total += reader.GetInt64(produced) + reader.GetInt64(approved) + reader.GetDecimal(value);
        }

        return total;
    }

    /// <summary>The same, plus two character fields converted to Int32 through the options.</summary>
    [Benchmark]
    public decimal TypedGettersWithConversions()
    {
        var options = new DbfReadOptions();
        options.Conversions["PA_IDADE"] = DbfFieldConversion.Int32();
        options.Conversions["PA_UFMUN"] = DbfFieldConversion.Int32();
        using var reader = DbSusFile.OpenDbcReader(_path, options);
        var produced = reader.GetOrdinal("PA_QTDPRO");
        var approved = reader.GetOrdinal("PA_QTDAPR");
        var value = reader.GetOrdinal("PA_VALAPR");
        var age = reader.GetOrdinal("PA_IDADE");
        var municipality = reader.GetOrdinal("PA_UFMUN");
        decimal total = 0;
        while (reader.Read())
        {
            total += reader.GetInt64(produced) + reader.GetInt64(approved) + reader.GetDecimal(value);
            if (!reader.IsDBNull(age))
            {
                total += reader.GetInt32(age);
            }

            total += reader.GetInt32(municipality);
        }

        return total;
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

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DbSus.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Run the benchmarks from inside the DbSus repository.");
    }
}
