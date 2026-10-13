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

Converting a `.dbc` into a `.dbf` file by path is all or nothing: the DBF is written to a temporary file and moved into place only at the end, so no partial file is left on failure. The overload that writes to a stream (`ConvertToDbf(Stream, Stream)`) writes as it decompresses: on failure, what was already written stays in that stream.

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

Blank numbers, dates and logicals, and dates made of zeros, are `DBNull`. A blank `C` field is an empty string, or `DBNull` with `DbfReadOptions.BlankStringsAsNull`. Content that does not parse throws `FormatException`, naming the field and the record but never the value; for `N`, `F` and `D` fields, `DbfReadOptions.UnconvertibleValues` can read it as null instead (see "Values that do not convert" below). Other field types (for example `M`, memo, whose data lives in a separate file) are not interpreted: `DbfField.IsSupported` is `false` and their stored bytes are available through `GetRawBytes`.

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

Only large SIA-PA files come in parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, …); smaller ones, such as `PAAP2004.dbc` and `PARR2407.dbc`, are a single file. `DbSusFile.FindParts` serves both cases: it returns the parts in order, or the single file. Parts are read as one table, after every part's header has been checked to have the same fields:

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
//   SIH (RD):               options.Conversions["DT_INTER"] = DbfFieldConversion.Date("yyyyMMdd");
//   SIA and CNES use the same yyyyMMdd layout, in their own date fields.
```

A converted field reports its new type everywhere (`GetFieldType`, schema, `GetValue`). Values that do not parse or do not fit throw `FormatException`, naming the field and record but never the value; `DbfReadOptions.UnconvertibleValues` does not apply to converted fields. The available conversions are `Int32`, `Int64`, `Decimal`, and `Date` with the layouts `yyyyMMdd`, `ddMMyyyy` and `yyyyMM`.

Date layout of character fields, as found in the files examined so far:

| Layout | Sources |
|---|---|
| `ddMMyyyy` | SIM, SINASC |
| `yyyyMMdd` | SIH, SIA, CNES |

SINAN stores dates in native `D` fields, which need no conversion. The layout cannot be inferred from field names, and a wrong one is not always caught: a `yyyyMMdd` value read as `ddMMyyyy` can still form a valid date, with the wrong year. Fields that hold a year and month (`yyyyMM`, such as a processing month) convert with `DbfFieldConversion.Date("yyyyMM")`, to the first day of that month. A year and a month stored in two separate fields (as SIH-RD does with `ANO_CMPT` and `MES_CMPT`) are not combined into one date: a conversion applies to one field.

## Values that do not convert

Some DATASUS files hold, in a numeric (`N` or `F`) or date (`D`) field, content that is not a number or a date. In the SINAN files read in October 2026 it was, in all fields but two, the field filled with asterisks; one file also had a date with slashes and a letter in a number ([DATASUS paths and file names](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md) says which files). A blank field and a date made of zeros are not this: they are null, always.

By default the reader does not guess what such content means:

- **These throw `FormatException`** on such a field: `GetValue`, the indexers, `GetValues`, `GetProviderSpecificValue`, the typed getters (`GetDecimal`, `GetDouble`, `GetInt16`, `GetInt32`, `GetInt64`, `GetDateTime`), `GetFieldValue<T>` with a nullable or a non-nullable type, and whatever reads every value, such as `DataTable.Load`. The message names the field, its type and the record, points at the option below, and never has the content.
- **These do not throw:** `Read`; `GetRawBytes`, which returns the bytes of the field as they are in the file; and the members that describe the fields. After the exception the reader is still usable: the other fields of the record and the records after it are read as usual. A projection that leaves the field out (`DbfReadOptions.Columns`) reads the table without the exception, `DataTable.Load` included.
- **`IsDBNull` returns `false`, and does not throw.** For a numeric field it only tells whether the field is blank. So the usual guard, `if (!reader.IsDBNull(i)) value = reader.GetDecimal(i);`, does not protect from this content: `GetDecimal` throws. For a date field it is the same.

`DbfReadOptions.UnconvertibleValues` changes that, for `N`, `F` and `D` fields:

| Value | A field filled with asterisks | Any other content that does not convert |
|---|---|---|
| `Throw` (the default) | `FormatException` | `FormatException` |
| `AsterisksAsNull` | null | `FormatException` |
| `Null` | null | null |

A field is filled with asterisks when, without the spaces at its ends, it holds `*` and nothing else.

Read as null means what a blank field means, in every member: `IsDBNull` returns `true`; `GetValue`, the indexers, `GetValues` and `GetProviderSpecificValue` give `DBNull.Value`; `GetFieldValue<T>` gives `null` for a nullable `T`; the typed getters, and `GetFieldValue<T>` of a non-nullable `T`, throw `InvalidCastException`, as they do for a blank field; `DataTable.Load` loads `DBNull`. `GetRawBytes` is the same in every mode. With `AsterisksAsNull`, for content that is not asterisks, `IsDBNull` returns `false` and the getter that follows throws, as in the default.

```csharp
using System;
using DbSus;
using DbSus.Dbf;

// A field filled with asterisks is read as null; any other content that does not convert still throws.
var options = new DbfReadOptions { UnconvertibleValues = DbfUnconvertibleValueHandling.AsterisksAsNull };
using var reader = DbSusFile.OpenReader("LTRR0610.dbc", options);
int beds = reader.GetOrdinal("QT_EXIST");
long total = 0, withoutNumber = 0;
while (reader.Read())
{
    if (reader.IsDBNull(beds))
    {
        withoutNumber++;                // blank, or filled with asterisks
    }
    else
    {
        total += reader.GetInt32(beds);
    }
}

Console.WriteLine($"{total} beds; {withoutNumber} records without the number");
```

- **The option is not for every field.** It does not apply to logical (`L`) fields, nor to character fields with a conversion (`DbfReadOptions.Conversions`): content of those that does not convert throws `FormatException` in every mode, and their message does not point at the option. No file read so far had such content in them.
- **A value read as null cannot be told from a blank field** through the typed members, and the reader does not count them. To know what a field holds, read it with `GetRawBytes`; the example below counts the values that do not convert, without the option.
- **With `Null`, a number that does not fit its field is null too.** An `N` field without decimals is an `int` or a `long`, and a number with a fraction stored in it does not convert. With `Null` it is null for every getter, `GetDecimal` included.
- **Cost.** In the default mode the option is looked at only where a conversion has already failed. In the two modes that read null, `IsDBNull` of a numeric field does more than look for a blank field: it looks for the asterisks and, with `Null`, tries the conversion, so `IsDBNull` followed by a getter converts the value twice. No mode allocates per record.

```csharp
using System;
using DbSus;

// Without the option: count the values of one field that do not convert.
using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
int beds = reader.GetOrdinal("QT_EXIST");
long doNotConvert = 0;
while (reader.Read())
{
    try
    {
        _ = reader.GetValue(beds);
    }
    catch (FormatException)
    {
        doNotConvert++;                                // the message names the field and the record, never the content
        int width = reader.GetRawBytes(beds).Length;   // the bytes of the field, as they are in the file
        _ = width;
    }
}

Console.WriteLine($"{doNotConvert} values do not convert");
```

## Character encoding

The code page of character fields comes from the header's language driver ID (LDID); when it is 0 or unknown, code page 1252 is used (change it with `FallbackEncoding`). To force an encoding, set `DbfReadOptions.Encoding`. Code-page encodings such as 850 are built into .NET Framework, but not into .NET Core and .NET 5 or later, where `Encoding.GetEncoding(850)` throws `NotSupportedException`.

LDID 0x00 (no driver) is therefore read as code page 1252. In October 2026 the bytes above 0x7F of character fields were counted in eighteen files of the catalog. Fourteen had none outside identifier fields that are not text, so the code page changes nothing in them. A SIHSUS-RD file marked 0x02 reads as code page 850 and two SIASUS-PA files marked 0x03 as 1252, as their LDID says. For LDID 0x00 the evidence is four bytes in one CNES file, which read as 1252; in the files marked 0x57 (ISO-8859-1), 0x58, 0x35 and 0x25 no such text was seen, so their code page is not validated. In the DBF tables inside the auxiliary archives of the server, which are not file types of the catalog, the LDID did not tell the code page: for such a table, look at the text and set `DbfReadOptions.Encoding`.

`DbfReader.EncodingSource` tells where the encoding of a reader came from: `Option` (`DbfReadOptions.Encoding` was set), `LanguageDriver` (a known LDID) or `Fallback`. `Fallback` covers LDID 0x00, an LDID that is not in the library's map (such as 0x35) and an LDID whose code page the runtime does not have; `Header.LanguageDriverId` shows which.

```csharp
using System;
using DbSus;
using DbSus.Dbf;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
if (reader.EncodingSource == DbfEncodingSource.Fallback)
{
    Console.WriteLine($"LDID 0x{reader.Header.LanguageDriverId:X2} gives no code page: text is read as {reader.Encoding.WebName}");
}
```

When certainty is needed, set `DbfReadOptions.Encoding`.

On .NET 5 or later and .NET Core, take the encoding from the provider, without registering anything process-wide:

```csharp net-only
using System.Text;
using DbSus.Dbf;

var options = new DbfReadOptions { Encoding = CodePagesEncodingProvider.Instance.GetEncoding(850) };
```

On .NET Framework, where code pages are built in:

```csharp netfx-only
using System.Text;
using DbSus.Dbf;

var options = new DbfReadOptions { Encoding = Encoding.GetEncoding(850) };
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

With Dapper, fields map to properties by name (checked by hand with `Parse<T>` and Dapper 2.1.89, on a mapping of two fields, as below):

```csharp no-check
using System.Linq;
using Dapper;
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
var beds = reader.Parse<BedCount>().ToList();

// public sealed class BedCount { public string CNES { get; set; } public int QT_EXIST { get; set; } }
```

### Loading into a database

The checksum of a `.dbc` is only known at the end (see the next section), so load into a staging table inside a transaction and commit only after the reader finished:

```csharp no-check
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

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record, nor does `GetChars` into a reused buffer when the encoding is single-byte (as are the code pages of the LDIDs found in DATASUS files), nor does `GetFieldValue<T>` with a non-nullable type; measured on .NET 8, 9 and 10 and on .NET Framework 4.8. **The nullable forms of `GetFieldValue<T>` (e.g. `GetFieldValue<int?>`) allocate on every call on .NET Framework and .NET 8**: about 24 bytes on .NET Framework 4.8.1 (x64), and 24 bytes (32 for `decimal?`) on .NET 8. On .NET 9 and .NET 10 they allocate nothing. Where they allocate, prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

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

`GetChars` decodes without allocating when the encoding is single-byte, which is the case for the code pages of the LDIDs found in DATASUS files (0x00, 0x02, 0x03, 0x57 and 0x58; 0x35, found in four SIM files, and 0x25, found in one SINAN file, are unknown and read with the fallback). With a double-byte code page (LDIDs 0x78, 0x79, 0x7A and 0x7B: code pages 950, 949, 936 and 932) or a multi-byte `DbfReadOptions.Encoding` such as UTF-8, it decodes the whole value into a new string first and allocates like `GetString`. Measured with this loop on .NET 10, a `.dbf` allocated nothing and a `.dbc` a single 4 KB buffer at the end of the data, to read what remains and verify the checksum; the conditions are in [performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md).

Codes stored as ASCII digits can also be compared without decoding at all, on the raw bytes: `reader.GetRawBytes(i).StartsWith("0301"u8)`. `u8` literals need C# 11 or later in your project (on .NET Framework, set `<LangVersion>` accordingly).
