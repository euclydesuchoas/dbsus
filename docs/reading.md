# Reading DBC and DBF files

This guide covers the `DbSus` package: opening files, value types, tables split into parts, conversions of character fields, encodings, ADO.NET, integrity and reading without allocation.

## Opening a file

```csharp
using System;
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");   // .dbc or .dbf, by extension
int cnes = reader.GetOrdinal("CNES");                      // look ordinals up once, outside the loop
int beds = reader.GetOrdinal("QT_EXIST");
while (reader.Read())
{
    Console.WriteLine($"{reader.GetString(cnes)}: {reader.GetInt32(beds)}");
}
```

The DBC is decompressed while you read: no temporary file, no full copy in memory. The reader is a `DbDataReader` that reads forward only.

Converting a `.dbc` into a `.dbf` is all or nothing: no partial file is left on failure.

```csharp
using DbSus.Dbc;

DbcFile.ConvertToDbf("LTRR0610.dbc", "LTRR0610.dbf");
```

### Value types

Types follow the DBF header:

| Field type | .NET type |
|---|---|
| `C` | `string`, without trailing spaces |
| `N` without decimals | `int` up to 9 digits wide, `long` up to 18, `decimal` beyond |
| `N` with decimals, `F` | `decimal` |
| `D` | `DateTime` (`yyyyMMdd`) |
| `L` | `bool` |

Blank numbers, dates and logicals, and dates made of zeros, are `DBNull`. A blank `C` field is an empty string, or `DBNull` with `DbfReadOptions.BlankStringsAsNull`. Content that does not parse throws `FormatException`, naming the field and the record but never the value. Other field types (for example `M`, memo, whose data lives in a separate file) are not interpreted: `DbfField.IsSupported` is `false` and their stored bytes are available through `GetRawBytes`.

### Who closes what

Methods that take a stream have a `leaveOpen` parameter (default `false`):

| You pass | `leaveOpen: false` (default) | `leaveOpen: true` |
|---|---|---|
| A path (`DbSusFile.OpenReader`, `DbfReader.Open(string)`, `DbcFile.OpenAsDbf(string)`, ...) | DbSus opens and closes the file; `leaveOpen` does not apply | — |
| A stream (`DbfReader.Open(Stream)`, `DbcFile.OpenAsDbf(Stream)`, `DbSusFile.OpenDbcReader(Stream)`) | Disposed with the returned reader or stream, **and also if the call throws** | Never disposed: it stays yours |
| Several streams, the parts of a table (`DbSusFile.OpenDbcReader(IEnumerable<DbcPart>)`, each part a stream and an optional name) | Each is disposed once its part has been read or the reader is disposed; **all of them if the call throws** | Never disposed |
| Two streams to `DbcFile.ConvertToDbf(Stream, Stream)` | Never disposed (there is no `leaveOpen`) | — |

So `using var reader = DbfReader.Open(File.OpenRead(path));` does not leak the file even when the header is invalid.

## Tables split into parts

SIA-PA files come in parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, …). They are read as one table, after every part's header has been checked to have the same fields:

```csharp
using DbSus;
using DbSus.Dbf;

var parts = DbSusFile.FindParts("PAMG2407a.dbc");   // a, b, c, ... in order
var options = new DbfReadOptions { Columns = new[] { "PA_UFMUN", "PA_VALAPR" } };
using var reader = DbSusFile.OpenDbcReader(parts, options);
while (reader.Read())
{
    // reader.RecordNumber counts across parts; reader.PartIndex tells the current part
}
```

Parts that are already open as streams go in as a list of `DbcPart`, in order. The name is optional and is used in messages:

```csharp
using System.IO;
using DbSus;
using DbSus.Dbc;
using DbSus.Dbf;

using var streamA = File.OpenRead("PAMG2407a.dbc");
using var streamB = File.OpenRead("PAMG2407b.dbc");
using var reader = DbSusFile.OpenDbcReader(
    new[] { new DbcPart("PAMG2407a.dbc", streamA), new DbcPart("PAMG2407b.dbc", streamB) },
    new DbfReadOptions { Columns = new[] { "PA_UFMUN", "PA_VALAPR" } },
    leaveOpen: true);   // the streams stay yours; without it, they are disposed with the reader
```

`new DbcPart(stream)` makes a part without a name (`part N` in messages).

## Numbers and dates stored as text

Many DATASUS fields are character fields holding numbers or dates, and date layouts differ between sources. Nothing is converted unless you ask:

```csharp
using DbSus;
using DbSus.Dbf;

var options = new DbfReadOptions { Columns = new[] { "CNES", "CODUFMUN", "COMPETEN" } };   // projection
options.Conversions["CODUFMUN"] = DbfFieldConversion.Int32();
options.Conversions["COMPETEN"] = DbfFieldConversion.Date("yyyyMM");   // a year and month
using var reader = DbSusFile.OpenReader("LTRR0610.dbc", options);

// Dates: name only fields that exist in the file you open, with that source's layout, e.g.
//   SIM (DO), SINASC (DN):  options.Conversions["DTOBITO"]  = DbfFieldConversion.Date("ddMMyyyy");
//   SIH (RD), SIA, CNES:    options.Conversions["DT_INTER"] = DbfFieldConversion.Date("yyyyMMdd");
```

A converted field reports its new type everywhere (`GetFieldType`, schema, `GetValue`). Values that do not parse or do not fit throw `FormatException`, naming the field and record but never the value. The available conversions are `Int32`, `Int64`, `Decimal`, and `Date` with the layouts `yyyyMMdd`, `ddMMyyyy` and `yyyyMM`.

Date layout of character fields, as found in the files examined so far:

| Layout | Sources |
|---|---|
| `ddMMyyyy` | SIM, SINASC |
| `yyyyMMdd` | SIH, SIA, CNES |

SINAN stores dates in native `D` fields, which need no conversion. The layout cannot be inferred from field names, and a wrong one is not always caught: a `yyyyMMdd` value read as `ddMMyyyy` can still form a valid date, with the wrong year. Fields that hold a year and month (`yyyyMM`, such as a processing month) convert with `DbfFieldConversion.Date("yyyyMM")`, to the first day of that month. A year and a month stored in two separate fields (as SIH-RD does with `ANO_CMPT` and `MES_CMPT`) are not combined into one date: a conversion applies to one field.

## Character encoding

The code page of character fields comes from the header's language driver ID (LDID); when it is 0 or unknown, code page 1252 is used (change it with `FallbackEncoding`). To force an encoding, set `DbfReadOptions.Encoding`. Code-page encodings such as 850 are built into .NET Framework, but not into .NET Core and .NET 5 or later, where `Encoding.GetEncoding(850)` throws `NotSupportedException`. There, take the encoding from the provider, without registering anything process-wide:

```csharp
using System.Text;
using DbSus.Dbf;

// .NET 5 or later and .NET Core:
var options = new DbfReadOptions { Encoding = CodePagesEncodingProvider.Instance.GetEncoding(850) };
// .NET Framework: new DbfReadOptions { Encoding = Encoding.GetEncoding(850) }
```

Calling `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` once at startup also works. DbSus itself never registers a provider.

## Reading only some fields

`DataTable.Load`, `SqlBulkCopy` and `GetValues` read every field the reader exposes. When you need only a few, list them in `DbfReadOptions.Columns`: the reader then exposes just those, in that order. When values are materialized as objects (`GetValues`, `DataTable`), a projection cuts allocation and time; with typed getters, it still saves the time spent decoding text, while the time spent decompressing stays. Measurements are in [performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md).

## ADO.NET: DataTable, SqlBulkCopy, Dapper

`DbfReader` is a `DbDataReader`:

```csharp
using System.Data;
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
var table = new DataTable();
table.Load(reader);
```

With Dapper, fields map to properties by name:

```csharp
using System.Linq;
using Dapper;
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
var beds = reader.Parse<BedCount>().ToList();

// public sealed class BedCount { public string CNES { get; set; } public int QT_EXIST { get; set; } }
```

### Loading into a database

The checksum of a `.dbc` is only known at the end (see the next section), so load into a staging table inside a transaction and commit only after the reader finished:

```csharp
using System.Threading.Tasks;
using DbSus;
using Microsoft.Data.SqlClient;

// Illustrative (compiled, not run against a server): the tables staging.pa and dbo.pa must already exist.
using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
using var transaction = connection.BeginTransaction();

using (var reader = DbSusFile.OpenDbcReader(DbSusFile.FindParts("PAMG2407a.dbc")))
using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction) { DestinationTableName = "staging.pa", BulkCopyTimeout = 0 })
{
    bulk.WriteToServer(reader);   // throws if a part's checksum fails: nothing is committed
}

using (var swap = new SqlCommand(
    "DELETE FROM dbo.pa WHERE competencia = '202407'; INSERT INTO dbo.pa SELECT * FROM staging.pa; TRUNCATE TABLE staging.pa;",
    connection,
    transaction))
{
    await swap.ExecuteNonQueryAsync();
}

transaction.Commit();   // reached only when every part was read and verified
```

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.
- The exception is a plain `InvalidDataException`, like other data errors. To react **specifically** to a checksum failure (for example to discard a cached copy), set `DbcReadOptions.ChecksumMismatch`: it is called with a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32) just before the exception, and only for checksum failures.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

## Reading without allocation

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record, nor does `GetChars` into a reused buffer when the encoding is single-byte (every code page chosen from the LDID). `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

`GetValues` boxes every value. To read every field without allocating, pick a typed getter per field once, from the schema, and dispatch on it for each record. Here the type of each field comes from `GetFieldType`, which also reflects any conversion you configured:

```csharp
using System;
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");

// One plan per field, made once.
var types = new TypeCode[reader.FieldCount];
for (var i = 0; i < types.Length; i++)
{
    types[i] = Type.GetTypeCode(reader.GetFieldType(i));   // byte[] (unsupported types) gives Object
}

var text = new char[256];   // reused for every text value; a C field holds at most 254 bytes
while (reader.Read())
{
    for (var i = 0; i < types.Length; i++)
    {
        if (reader.IsDBNull(i))
        {
            continue;
        }

        switch (types[i])
        {
            case TypeCode.String:
                var length = (int)reader.GetChars(i, 0, text, 0, text.Length);
                ReadOnlySpan<char> value = text.AsSpan(0, length);   // valid until the next GetChars
                break;
            case TypeCode.Int32: _ = reader.GetInt32(i); break;
            case TypeCode.Int64: _ = reader.GetInt64(i); break;
            case TypeCode.Decimal: _ = reader.GetDecimal(i); break;
            case TypeCode.DateTime: _ = reader.GetDateTime(i); break;
            case TypeCode.Boolean: _ = reader.GetBoolean(i); break;
            default: _ = reader.GetRawBytes(i); break;   // unsupported types: the stored bytes
        }
    }
}
```

`GetChars` decodes without allocating when the encoding is single-byte, which is the case for every code page chosen from the LDID. Measured with this loop on .NET 10, a `.dbf` allocated nothing and a `.dbc` a single 4 KB buffer at the end of the data, to read what remains and verify the checksum; the conditions are in [performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md).

Codes stored as ASCII digits can also be compared without decoding at all, on the raw bytes: `reader.GetRawBytes(i).StartsWith("0301"u8)`. `u8` literals need C# 11 or later in your project (on .NET Framework, set `<LangVersion>` accordingly).
