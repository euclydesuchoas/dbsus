# DbSus

A .NET library for reading the `.dbc` (compressed dBase) and `.dbf` files distributed by DATASUS, in fully managed C#, with no dependency on R, Python or native binaries.

> **Disclaimer:** DbSus is an independent project. It is not affiliated with, endorsed by or maintained by the Brazilian Ministry of Health (Ministério da Saúde) or DATASUS.

## Status

Preview. Nothing is published on NuGet yet and the API may still change.

## Packages

| Package | Purpose |
|---|---|
| `DbSus` | Core: DBC decompression and DBF reading (streaming, typed getters, `DbDataReader`, tables split into parts). |
| `DbSus.Client` | Direct access to the DATASUS FTP server: listing, split-table discovery, resumable downloads, optional cache. |

Target frameworks: `net10.0`, `net472` and `netstandard2.0`. Supported runtimes: .NET 8 or later, .NET Framework **4.7.2** or later (it gets the `net472` build), and other `netstandard2.0` platforms.

## Usage

### Read a `.dbc`

```csharp
using DbSus;

using var reader = DbSusFile.OpenReader("LTRR0610.dbc");   // .dbc or .dbf, by extension
var cnes = reader.GetOrdinal("CNES");                      // look ordinals up once, outside the loop
var beds = reader.GetOrdinal("QT_EXIST");
while (reader.Read())
{
    string code = reader.GetString(cnes);
    int count = reader.GetInt32(beds);
}
```

The DBC is decompressed while you read: no temporary file, no full copy in memory. Types follow the DBF header: `C` → `string`, `N` → `int`/`long`/`decimal` (by width and decimals), `D` → `DateTime`, `L` → `bool`; blank numbers and dates are `DBNull`.

### Convert `.dbc` into `.dbf`

```csharp
using DbSus.Dbc;

DbcFile.ConvertToDbf("LTRR0610.dbc", "LTRR0610.dbf");   // all-or-nothing: no partial file on failure
```

### Tables split into parts

SIA-PA files come in parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, …). They are read as one table, after every part's header has been checked to have the same fields:

```csharp
var parts = DbSusFile.FindParts("PAMG2407a.dbc");             // a, b, c, ... in order
using var reader = DbSusFile.OpenDbcReader(parts, new DbfReadOptions { Columns = ["PA_UFMUN", "PA_VALAPR"] });
while (reader.Read())
{
    // reader.RecordNumber counts across parts; reader.PartIndex tells the current part
}
```

### Numbers and dates stored as text

Many DATASUS fields are character fields holding numbers or dates, and date layouts differ between sources. Nothing is converted unless you ask:

```csharp
var options = new DbfReadOptions { Columns = ["CNES", "CODUFMUN", "COMPETEN"] };   // projection
options.Conversions["CODUFMUN"] = DbfFieldConversion.Int32();
using var reader = DbSusFile.OpenReader("LTRR0610.dbc", options);

// Dates: name only fields that exist in the file you open, with that source's layout, e.g.
//   SIM (DO), SINASC (DN):  options.Conversions["DTOBITO"]  = DbfFieldConversion.Date("ddMMyyyy");
//   SIH (RD), SIA, CNES:    options.Conversions["DT_INTER"] = DbfFieldConversion.Date("yyyyMMdd");
```

A converted field reports its new type everywhere (`GetFieldType`, schema, `GetValue`). Values that do not parse or do not fit throw `FormatException`, naming the field and record but never the value.

Date layout of character fields, as found in the files examined so far:

| Layout | Sources |
|---|---|
| `ddMMyyyy` | SIM, SINASC |
| `yyyyMMdd` | SIH, SIA, CNES |

SINAN stores dates in native `D` fields, which need no conversion. The layout cannot be inferred from field names, and a wrong one is not always caught: a `yyyyMMdd` value read as `ddMMyyyy` can still form a valid date, with the wrong year. Fields that hold only a year and month (`yyyyMM`, such as a processing month) have no date conversion; read them as `Int32`.

### Character encoding

The code page of character fields comes from the header's language driver ID (LDID); when it is 0 or unknown, code page 1252 is used (change it with `FallbackEncoding`). To force an encoding, set `DbfReadOptions.Encoding`. Code-page encodings such as 850 are built into .NET Framework, but not into .NET (Core) and .NET 5 or later, where `Encoding.GetEncoding(850)` throws `NotSupportedException`. There, take the encoding from the provider, without registering anything process-wide:

```csharp
using System.Text;

var options = new DbfReadOptions { Encoding = CodePagesEncodingProvider.Instance.GetEncoding(850) };
// or, once at startup: Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
```

DbSus itself never registers a provider.

### ADO.NET: `DataTable`, `SqlBulkCopy`, Dapper

`DbfReader` is a `DbDataReader`:

```csharp
using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
var table = new DataTable();
table.Load(reader);
```

### Reading only some fields

`DataTable.Load`, `SqlBulkCopy` and `GetValues` read every field the reader exposes. When you need only a few, list them in `DbfReadOptions.Columns`: the reader then exposes just those, in that order.

How much that helps depends on how the values are read. Measured on one file (an SIH-RD month: 132,337 records of 113 fields), on one machine (the one of the [benchmarks](#performance), Windows 11, Release, .NET SDK 10.0.401), each variant run twice, each run in a new process:

| Reading | Time (2 runs) | Allocated | Peak working set |
|---|---|---|---|
| `GetValues`, all 113 fields | 689 and 681 ms | 377 MiB (2,984 B per record) | 77 MiB |
| `GetValues`, 3 projected fields | 487 and 483 ms | 14.3 MiB (114 B per record) | 42 MiB |
| Typed getters chosen by `GetFieldType`, text as raw bytes, all 113 fields | 574 and 579 ms | 201 KiB (1.6 B per record) | 28.5 MiB |
| The same, 3 projected fields | 468 and 487 ms | 201 KiB | 28 MiB |

When values are materialised as objects (`GetValues`, `DataTable`), a projection cuts allocation a lot and the time somewhat. With typed getters and text read as raw bytes, allocation is already close to zero and a projection changes little: most of the time is decompression, which a projection does not reduce. For comparison, the same file already decompressed (`.dbf`), with 3 fields read through `GetValues`, took 173 ms from disk and 63 ms from the operating system's file cache.

### Loading into a database

The checksum of a `.dbc` is only known at the end (see [Integrity](#integrity-is-confirmed-only-at-the-end)), so load into a staging table inside a transaction and commit only after the reader finished:

```csharp
using var connection = new SqlConnection(connectionString);
await connection.OpenAsync();
using var transaction = connection.BeginTransaction();

using (var reader = DbSusFile.OpenDbcReader(DbSusFile.FindParts("PAMG2407a.dbc")))
using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction) { DestinationTableName = "staging.pa", BulkCopyTimeout = 0 })
{
    bulk.WriteToServer(reader);    // throws if any part's checksum fails: nothing is committed
}

using (var swap = new SqlCommand(
    "DELETE FROM dbo.pa WHERE competencia = '202407'; INSERT INTO dbo.pa SELECT * FROM staging.pa; TRUNCATE TABLE staging.pa;",
    connection,
    transaction))
{
    await swap.ExecuteNonQueryAsync();
}

transaction.Commit();              // reached only when every part was read and verified
```

### Downloading from DATASUS (`DbSus.Client`)

```csharp
using DbSus.Client;

// Thread-safe: register once, e.g. services.AddSingleton(client).
var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = @"D:\datasus-cache" });
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));   // every method takes a token
var token = timeout.Token;

// Directory listing (names, sizes; reused for ListingCacheDuration, 10 minutes by default).
var entries = await client.ListAsync("/SIHSUS/200801_/Dados", token);

// Exact size and modification time (UTC) of one file.
var info = await client.GetInfoAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", token);

// The parts of a split table, from the server's listing (which shows a missing last part).
var parts = await client.FindPartsAsync("/SIASUS/200801_/Dados/PAMG2407.dbc", token);

// Schema and record count from the first few kilobytes, without downloading the file.
var header = await client.ReadHeaderAsync("/CNES/200508_/Dados/LT/LTRR0610.dbc", token);

// Download to a local path (a cache entry here), reused while the server's size and time are unchanged.
string local = await client.DownloadAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", token);

// Every part of SIA-PA MG 2024-07, downloaded once, then read as one table.
using var reader = await client.OpenReaderAsync("/SIASUS/200801_/Dados/PAMG2407.dbc", cancellationToken: token);
```

- Paths are relative to `/dissemin/publicos` (`RootPath`). URLs, `..`, backslashes and names that are invalid on Windows or Linux are rejected.
- At most two connections by default (`MaxConnections`, four at most), to be gentle with the server; the limit applies per instance.
- **Retries:** transient failures (dropped connections, timeouts, FTP 4xx replies, truncated transfers) are retried up to `MaxRetries` times (4 by default). The nominal wait starts at `InitialRetryDelay` (1 second by default) and doubles each time, up to 30 seconds; each actual wait is a random fraction, between half and all, of the nominal one. A missing file (FTP 550) is never retried. Retries are not reported through any event or callback.
- **Downloads** resume after a dropped connection, restart from zero if the file changed on the server, and are verified (CRC-32) before being used or cached (`VerifyOnDownload`). Even with a cache, every download first asks the server for the file's size and time, to detect republished files.
- **The cache is optional.** Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy. Each entry has a small `entry.lock` file used to coordinate threads and processes; pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it), and `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, for when no other process is downloading into the same cache.
- **Without a cache** (`CacheDirectory` not set), each download goes to its own folder under the system temporary directory, `Path.GetTempPath()/DbSus/downloads/<random id>/` (on Windows usually `%TEMP%\DbSus\downloads`). That location cannot be changed: **to keep downloaded microdata in a folder you control, set `CacheDirectory`.** Files opened by `OpenReaderAsync` are deleted when the reader is disposed; a file returned by `DownloadAsync` is yours to delete. Folders left behind for more than 24 hours, whose files are not open, are removed later.

Exceptions:

| Exception | When |
|---|---|
| `ArgumentException` | The path is not acceptable (the message says why). |
| `FileNotFoundException` | The file does not exist (FTP 550), or a part before the last one is missing. |
| `IOException` | Retries were exhausted (the message says after how many attempts), the server refused the request permanently (for example a login refusal), there is not enough disk space, part names on the server are ambiguous, or an invalid cached copy is still open by a reader. These cases share the type; only the message tells them apart. |
| `InvalidDataException` | A downloaded `.dbc` failed its CRC-32 check twice, a remote header is invalid, or, while reading, the data is corrupt or fails its checksum at the end. |
| `TimeoutException` | Another thread or process held the same cache entry for longer than `LockTimeout` (10 minutes by default). |
| `OperationCanceledException` | The cancellation token was canceled. |

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.
- The exception is a plain `InvalidDataException`, like other data errors. To react **specifically** to a checksum failure (for example to discard a cached copy), set `DbcReadOptions.ChecksumMismatch`: it is called with a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32) just before the exception, and only for checksum failures.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

## Allocation-free reading

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record. `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

## Performance

Measured with BenchmarkDotNet (`benchmarks/DbSus.Benchmarks`, Release, .NET 10, AMD Ryzen 9 9950X3D) on one large SIA-PA file: 126 MB compressed, 1.17 GB of DBF, 2,956,469 records of 60 fields.

| What | DBF throughput | Allocated per record |
|---|---|---|
| Decompression only | 311 MB/s | 0.05 B |
| Decompression and reading the records | 309 MB/s | 0.06 B |
| Plus 3 typed getters per record | 299 MB/s | 0.06 B |
| Plus 2 converted character fields | 293 MB/s | 0.06 B |
| Every value of every field (`GetValues`, boxed) | 189 MB/s | 1,867 B |

The small allocations of the first four rows are fixed buffers, not per-record costs. Decompression dominates; when files come from the DATASUS server, the network is usually slower still.

In a real-world test, a complete SIA-PA month split into two parts, about 8 million records, was read in about 11 seconds, with about 0 bytes allocated per record, a memory peak of 34 MiB and no garbage collection.

## .NET Framework

The minimum is .NET Framework 4.7.2, which uses the packages' `net472` build. If an application still fails on the first read with a `FileLoadException` for `System.Memory` (for example an unusual build setup that resolves the `netstandard2.0` build instead), add a binding redirect to its `app.config`. `newVersion` must be the assembly version of the `System.Memory.dll` the application ships; for example 4.0.5.0, from the `System.Memory` 4.6.3 package:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />
</dependentAssembly>
```

The packages accept `System.Memory` 4.5.3 or later, `System.Text.Encoding.CodePages` 4.4.0 or later and `Microsoft.Bcl.AsyncInterfaces` 1.0.0 or later, so an application that already references one of them, at those versions or later, keeps its own version without a downgrade error (NU1605).

## Security note

The DATASUS server only offers **plain FTP** (it refuses TLS), so traffic is unencrypted and could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

## License

[MIT](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE). The DBC decompressor is a C# port of Mark Adler's `blast.c` (zlib license); see [THIRD-PARTY-NOTICES.md](https://github.com/euclydesuchoas/dbsus/blob/main/THIRD-PARTY-NOTICES.md).
