// An application that uses only DbSus, published trimmed and with Native AOT in CI (see README.md).
// Every input is built in memory: no DATASUS file is read. Prints only numbers and type names, so
// the output of the published application can be compared with that of a normal run, and exits
// with a non-zero code on the first failure.
using System.Data.Common;
using System.Globalization;
using System.Text;
using DbSus;
using DbSus.Dbc;
using DbSus.Dbf;
using DbSus.Tests.TestSupport;

try
{
    // DBC with uncoded literals only, with and without the end-of-file marker.
    foreach (var marker in new[] { true, false })
    {
        var dbf = SyntheticDbc.BuildDbf(marker);
        var dbc = SyntheticDbc.BuildDbc(dbf);
        var output = ReadAll(DbcFile.OpenAsDbf(new MemoryStream(dbc)));
        Check("literals", output.AsSpan().SequenceEqual(dbf), "output differs from the DBF");
        Print($"literals endOfFileMarker={marker} dbc={dbc.Length} dbf={output.Length}");
    }

    // A stored CRC-32 that does not match: the read that reaches the end throws.
    var mismatch = SyntheticDbc.BuildDbc(SyntheticDbc.BuildDbf(true), checksum: 0x12345678);
    try
    {
        ReadAll(DbcFile.OpenAsDbf(new MemoryStream(mismatch)));
        throw new InvalidOperationException("checksum: no exception");
    }
    catch (InvalidDataException exception)
    {
        Print($"checksum mismatch -> {exception.GetType().FullName}");
    }

    // Copies: the three dictionary sizes, with uncoded and coded literals.
    var table = BuildTable();
    var headerLength = table[8] | (table[9] << 8);
    var body = table.AsSpan(headerLength).ToArray();
    var crc = ReferenceCrc32.Compute(table);
    byte[] last = [];
    foreach (var dictionaryBits in new[] { 4, 5, 6 })
    {
        foreach (var codedLiterals in new[] { false, true })
        {
            var tokens = Tokenize(body, dictionaryBits);
            Check("tokens", ImplodeEncoder.Expand(tokens).AsSpan().SequenceEqual(body), "tokens do not describe the data");
            var copies = tokens.Where(t => t.IsCopy).ToList();
            Check("longest copy", copies.Any(t => t.Length == 518), "no copy of 518");
            Check("shortest copy", copies.Any(t => t.Length == 2), "no copy of 2");

            var dbc = new MemoryStream();
            dbc.Write(table, 0, headerLength);
            dbc.Write(BitConverter.GetBytes(crc), 0, 4);
            var implode = ImplodeEncoder.Encode(tokens, codedLiterals, dictionaryBits);
            dbc.Write(implode, 0, implode.Length);
            last = dbc.ToArray();

            var output = ReadAll(DbcFile.OpenAsDbf(new MemoryStream(last)));
            Check("copies", output.AsSpan().SequenceEqual(table), "output differs from the DBF");
            Print($"copies dictionary={64 << dictionaryBits} codedLiterals={codedLiterals} tokens={tokens.Count} copies={copies.Count} copies518={copies.Count(t => t.Length == 518)} copies2={copies.Count(t => t.Length == 2)} maxDistance={copies.Max(t => t.Distance)} dbc={last.Length} dbf={output.Length}");
        }
    }

    // The reader over the last DBC: typed values, a conversion, the schema table and the code page
    // the language driver ID gives.
    var options = new DbfReadOptions();
    options.Conversions["CODE"] = DbfFieldConversion.Int32();
    using (var reader = DbSusFile.OpenDbcReader(new MemoryStream(last), options))
    {
        Print($"reader fields={reader.FieldCount} types={string.Join(",", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetFieldType(i).FullName))}");
        var schema = reader.GetSchemaTable();
        Print($"schema rows={schema.Rows.Count} columns={schema.Columns.Count} dataTypes={string.Join(",", schema.Rows.Cast<System.Data.DataRow>().Select(r => ((Type)r[SchemaTableColumn.DataType]).FullName))}");
        Check("code page", reader.Encoding.CodePage == 850 && reader.EncodingSource == DbfEncodingSource.LanguageDriver, reader.Encoding.CodePage.ToString(CultureInfo.InvariantCulture));

        long records = 0, codes = 0, counts = 0, flags = 0, cedillas = 0, nonAscii = 0, textLength = 0;
        decimal values = 0;
        int firstDate = 0, lastDate = 0;
        var code = reader.GetOrdinal("CODE");
        var city = reader.GetOrdinal("CITY");
        var value = reader.GetOrdinal("VALUE");
        var count = reader.GetOrdinal("COUNT");
        var date = reader.GetOrdinal("DATE");
        var flag = reader.GetOrdinal("FLAG");
        var note = reader.GetOrdinal("NOTE");
        while (reader.Read())
        {
            records++;
            codes += reader.GetInt32(code);
            values += reader.GetDecimal(value);
            counts += reader.GetInt32(count);
            flags += reader.GetBoolean(flag) ? 1 : 0;
            var day = int.Parse(reader.GetDateTime(date).ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            firstDate = firstDate == 0 ? day : firstDate;
            lastDate = day;
            foreach (var c in reader.GetString(city) + (reader.IsDBNull(note) ? string.Empty : reader.GetString(note)))
            {
                textLength++;
                nonAscii += c > 0x7F ? 1 : 0;
                cedillas += c == 'ç' ? 1 : 0;
            }
        }

        Check("records", records == Records, records.ToString(CultureInfo.InvariantCulture));
        Check("codes", codes == Enumerable.Range(0, Records).Sum(i => SourceRow(i) + 1), codes.ToString(CultureInfo.InvariantCulture));
        Check("cedillas", cedillas == nonAscii && cedillas == Enumerable.Range(0, Records).Count(i => SourceRow(i) % 3 == 0), cedillas.ToString(CultureInfo.InvariantCulture));
        Print($"encoding codePage={reader.Encoding.CodePage} source={reader.EncodingSource} nonAscii={nonAscii} cedillas={cedillas} textLength={textLength}");
        Print($"values records={records} codes={codes} values={values.ToString(CultureInfo.InvariantCulture)} counts={counts} flags={flags} firstDate={firstDate} lastDate={lastDate}");
    }

    // DBC to DBF on disk, then the plain DBF reader.
    var work = Path.Combine(Path.GetTempPath(), "dbsus-trim-consumer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    try
    {
        var dbcPath = Path.Combine(work, "TABLE.dbc");
        var dbfPath = Path.Combine(work, "TABLE.dbf");
        File.WriteAllBytes(dbcPath, last);
        DbcFile.ConvertToDbf(dbcPath, dbfPath);
        Check("converted", File.ReadAllBytes(dbfPath).AsSpan().SequenceEqual(table), "converted DBF differs");
        using var reader = DbfReader.Open(dbfPath);
        var records = 0;
        while (reader.Read())
        {
            records++;
        }

        Check("converted records", records == Records, records.ToString(CultureInfo.InvariantCulture));
        Print($"converted records={records} recordCount={reader.Header.RecordCount}");
    }
    finally
    {
        Directory.Delete(work, recursive: true);
    }

    Print($"OK");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAILED: {exception.GetType().FullName}: {exception.Message}");
    return 1;
}

static void Print(FormattableString line) => Console.WriteLine(line.ToString(CultureInfo.InvariantCulture));

static void Check(string what, bool condition, string detail)
{
    if (!condition)
    {
        throw new InvalidOperationException($"{what}: {detail}");
    }
}

static byte[] ReadAll(Stream stream)
{
    using (stream)
    {
        var output = new MemoryStream();
        stream.CopyTo(output, 4096);
        return output.ToArray();
    }
}

// The record a row repeats: rows 200 to 239 are all copies of row 200, for copies of 518 bytes.
static int SourceRow(int i) => i is >= 200 and < 240 ? 200 : i;

// A table in code page 850 (LDID 0x02): character, numeric, date and logical fields; a run of
// identical records; "ç" (0x87 in code page 850) in every third source row.
static byte[] BuildTable()
{
    string[] names = ["ALPHA", "BETA", "GAMMA", "DELTA", "EPSILON"];
    var builder = new DbfBuilder { LanguageDriverId = 0x02 }
        .Field("CODE", 'C', 6)
        .Field("NAME", 'C', 12)
        .Field("CITY", 'C', 16)
        .Field("VALUE", 'N', 10, 2)
        .Field("COUNT", 'N', 4)
        .Field("DATE", 'D', 8)
        .Field("FLAG", 'L', 1)
        .Field("NOTE", 'C', 254);
    for (var i = 0; i < Records; i++)
    {
        var k = SourceRow(i);
        byte[] city = k % 3 == 0 ? [.. "CAMA"u8, 0x87, .. "ARI"u8] : [.. "ARACAJU"u8];
        builder.Record(
            (k + 1).ToString("D6", CultureInfo.InvariantCulture),
            names[k % names.Length],
            city,
            ((k * 37 % 10000) / 100m).ToString("F2", CultureInfo.InvariantCulture).PadLeft(10),
            (k % 50).ToString(CultureInfo.InvariantCulture).PadLeft(4),
            new DateTime(2020, 1, 1).AddDays(k * 7).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            k % 4 == 0 ? "T" : "F",
            k % 10 == 0 ? "REVIEWED" : string.Empty);
    }

    return builder.Build();
}

// Greedy tokens for the implode encoder: the longest earlier match (up to 518 bytes, within the
// dictionary), else a two-byte match within 256 bytes, else a literal.
static List<ImplodeToken> Tokenize(byte[] data, int dictionaryBits)
{
    var tokens = new List<ImplodeToken>();
    var head = new Dictionary<int, int>();
    var previous = new int[data.Length];
    var position = 0;
    while (position < data.Length)
    {
        int length = 0, distance = 0;
        if (position + 2 < data.Length && head.TryGetValue(Key(data, position), out var candidate))
        {
            for (var steps = 0; candidate >= 0 && steps < 64; steps++, candidate = previous[candidate])
            {
                var d = position - candidate;
                if (d > ImplodeEncoder.MaxDistance(3, dictionaryBits))
                {
                    break;
                }

                var l = 0;
                while (l < 518 && position + l < data.Length && data[candidate + l] == data[position + l])
                {
                    l++;
                }

                if (l > length)
                {
                    (length, distance) = (l, d);
                }
            }
        }

        if (length < 3)
        {
            (length, distance) = (1, 0);
            for (var d = 1; d <= Math.Min(256, position) && position + 1 < data.Length; d++)
            {
                if (data[position - d] == data[position] && data[position - d + 1] == data[position + 1])
                {
                    (length, distance) = (2, d);
                    break;
                }
            }
        }

        tokens.Add(length == 1 ? ImplodeToken.Of(data[position]) : ImplodeToken.Copy(length, distance));
        for (var end = position + length; position < end; position++)
        {
            if (position + 2 < data.Length)
            {
                var key = Key(data, position);
                previous[position] = head.TryGetValue(key, out var p) ? p : -1;
                head[key] = position;
            }
        }
    }

    return tokens;

    static int Key(byte[] data, int i) => data[i] | (data[i + 1] << 8) | (data[i + 2] << 16);
}

partial class Program
{
    private const int Records = 300;
}
