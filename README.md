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

Parts that are already open as streams go in as a list of `DbcPart`, in order; the name, optional, is used in messages:

```csharp
using var reader = DbSusFile.OpenDbcReader(
    [new DbcPart("PAMG2407a.dbc", streamA), new DbcPart("PAMG2407b.dbc", streamB)],
    new DbfReadOptions { Columns = ["PA_UFMUN", "PA_VALAPR"] },
    leaveOpen: true);   // the streams stay yours; without it, they are disposed with the reader
```

`new DbcPart(stream)` makes a part without a name (`part N` in messages).

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

SINAN stores dates in native `D` fields, which need no conversion. The layout cannot be inferred from field names, and a wrong one is not always caught: a `yyyyMMdd` value read as `ddMMyyyy` can still form a valid date, with the wrong year. Fields that hold a year and month (`yyyyMM`, such as a processing month) convert with `DbfFieldConversion.Date("yyyyMM")`, to the first day of that month. A year and a month stored in two separate fields (as SIH-RD does with `ANO_CMPT` and `MES_CMPT`) are not combined into one date: a conversion applies to one field.

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

How much that helps depends on how the values are read. Measured on one file (an SIH-RD month: 132,337 records of 113 fields), on one machine (the one of the [benchmarks](#performance), Windows 11, Release, .NET SDK 10.0.401), each run in a new process. A first session ran four variants twice each:

| Reading | Time (2 runs) | Allocated | Peak working set |
|---|---|---|---|
| `GetValues`, all 113 fields | 689 and 681 ms | 377 MiB (2,984 B per record) | 77 MiB |
| `GetValues`, 3 projected fields | 487 and 483 ms | 14.3 MiB (114 B per record) | 42 MiB |
| Typed getters chosen by `GetFieldType`, text as raw bytes, all 113 fields | 574 and 579 ms | 201 KiB (1.6 B per record) | 28.5 MiB |
| The same, 3 projected fields | 468 and 487 ms | 201 KiB | 28 MiB |

A second session added text decoded through `GetChars` into a reused buffer:

| Reading | All 113 fields | 3 projected fields |
|---|---|---|
| `GetValues` | 0.68 s, 377 MiB | 0.48 to 0.50 s, 14 MiB |
| Typed getters, text as raw bytes | 0.57 s | 0.47 s |
| Typed getters, text decoded with `GetChars` | 0.68 s, 205 KiB | 0.48 s, 205 KiB |

On this file and this machine: when values are materialised as objects (`GetValues`, `DataTable`), a projection cut allocation from 377 MiB to about 14 MiB and the time by about 30%. With typed getters and text decoded through `GetChars`, allocation was already close to zero and a projection still saved about 30% of the time, since decoding text costs time even without allocating. With text left as raw bytes it saved less (0.57 s to 0.47 s): most of the remaining time is decompression, which a projection does not reduce. For comparison, the same file already decompressed (`.dbf`), with 3 fields read through `GetValues`, took 173 ms from disk and 63 ms from the operating system's file cache.

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

// The parts of a split table, from the server's listing, checked by asking for the part after the last one.
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
- **Retries:** transient failures (dropped connections, timeouts, FTP 4xx replies, truncated transfers) are retried up to `MaxRetries` times (4 by default). The nominal wait starts at `InitialRetryDelay` (1 second by default) and doubles each time, up to 30 seconds; each actual wait is a random fraction, between half and all, of the nominal one. A missing file (FTP 550) is never retried. To log retries, set `OnRetry`: it receives a description of the operation (for people to read), the remote path, the attempt that failed, the wait, the failure and its FTP reply code, before each wait; its exceptions are ignored.
- **Downloads** resume after a dropped connection, restart from zero if the file changed on the server, and are verified (CRC-32) before being used or cached (`VerifyOnDownload`). Even with a cache, every download first asks the server for the file's size and time, to detect republished files.
- **The cache is optional.** Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy. Each entry has a small `entry.lock` file used to coordinate threads and processes; pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it), and `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, for when no other process is downloading into the same cache.
- **Without a cache** (`CacheDirectory` not set), each download goes to its own folder under the system temporary directory, `Path.GetTempPath()/DbSus/downloads/<random id>/` (on Windows usually `%TEMP%\DbSus\downloads`). That location cannot be changed: **to keep downloaded microdata in a folder you control, set `CacheDirectory`.** Files opened by `OpenReaderAsync` are deleted when the reader is disposed; a file returned by `DownloadAsync` is yours to delete. Folders left behind for more than 24 hours, whose files are not open, are removed later.

Exceptions:

| Exception | When |
|---|---|
| `ArgumentException` | The path is not acceptable (the message says why). |
| `FileNotFoundException` | The file does not exist (FTP 550), or a part before the last one is missing. |
| `DatasusTransferException` | The base of the next two: a transfer with the server failed. Catch it to handle both; `Attempts`, `FtpReplyCode` and `RemotePath` are defined here. |
| `DatasusRetriesExhaustedException` | Transient failures persisted after every retry. `Attempts`, `FtpReplyCode` (e.g. `421`) and `RemotePath` tell the details. |
| `DatasusRequestRefusedException` | The server refused the request with a permanent reply other than 550 (e.g. `530`); not retried. Same properties. |
| `InsufficientDiskSpaceException` | Not enough free space for the file (`Directory`, `RequiredBytes`, `AvailableBytes`), checked before the transfer. |
| `IOException` | Part names on the server are ambiguous, or an invalid cached copy is still open by a reader. The three exceptions above also derive from `IOException`, so a `catch (IOException)` still sees them. |
| `InvalidDataException` | A downloaded `.dbc` failed its CRC-32 check twice, a remote header is invalid, or, while reading, the data is corrupt or fails its checksum at the end. |
| `TimeoutException` | Another thread or process held the same cache entry for longer than `LockTimeout` (10 minutes by default). |
| `OperationCanceledException` | The cancellation token was canceled. |

#### When the connection fails

FTP uses two connections: the control connection, to port 21 by default, and a data connection for each listing and each download, to a port the server chooses (passive mode). A failure message names the step (`connecting to`, `listing`, `reading the size and date of`, `opening ... for reading`) with the host and port. The underlying FTP library reports a failed connection attempt the same way for both connections (for example `Timed out trying to connect to IP #1`, the number being the position of the address tried), so the message says what is known: whether the control connection was reopened during the step and, for listings and downloads, that a blocked passive data connection is a possible cause.

What the client does, from its code:

- **Passive mode only.** It uses `EPSV` when the server lists it in its `FEAT` reply, and `PASV` otherwise. It switches from `EPSV` to `PASV` only when the server answers `EPSV` with an error reply (4xx or 5xx) and the control connection uses IPv4; a data connection that times out or is refused after `EPSV` is not retried with `PASV`. With `EPSV` the data connection goes to the same address as the control connection; with `PASV`, to the address the server announces (or to the control connection's host, when the announced address is a private one).
- **IPv4 and IPv6.** The host name's addresses are tried in the order the system's DNS returns them.
- **Time.** Each connection attempt waits up to `ConnectTimeout` (30 seconds by default) per address, and a failed step is retried `MaxRetries` times (4 by default), the connection that failed being discarded, so a server that never answers can take minutes before the exception.
- **Settings.** The data connection mode and the address family cannot be changed through `DatasusClientOptions`; `Host`, `Port`, `ConnectTimeout` and `ReadTimeout` can.

Worst case, calculated from the default options, for a listing or a download whose data connection fails: 5 attempts, with waits of 7.5 to 15 seconds in all between them; the data connection goes to a single address, once per attempt.

| The data connection... | Per attempt | Until the exception |
|---|---|---|
| never answers | `ConnectTimeout`: 30 s | 2 min 37.5 s to 2 min 45 s |
| opens, but no listing data arrives | `ReadTimeout` reading (60 s), then `ReadTimeout` waiting for the server's final reply (60 s): 120 s | 10 min 7.5 s to 10 min 15 s |
| opens, but no download data arrives | `ReadTimeout` (60 s); the client then closes the connection, so there is no wait for a final reply | 5 min 7.5 s to 5 min 15 s |

The same formulas were checked against a local test server with small timeouts (`ConnectTimeout` 200 ms, `ReadTimeout` 300 ms, `InitialRetryDelay` 100 ms), on .NET 10 and .NET Framework 4.8. `ReadTimeout` limits each wait for data, not a whole transfer: a download that keeps receiving data slowly is never cut by it. On every runtime, .NET Framework included, these waits end at their timeouts, and cancelling the token ends them at once (the connection is closed and not reused). In a web request or any call with a deadline, lower `MaxRetries`, `ConnectTimeout` and `ReadTimeout`, and pass a cancellation token that carries the deadline.

**A known limit of listings.** A listing counts only when the server confirms its end (a `226` reply after the data); one without that confirmation is a transient failure and is not cached. But if the client's read of the listing fails while the server still answers `226`, the client cannot tell, and a partial listing can pass. For split tables, `FindPartsAsync` reduces the risk: it asks the server directly (`SIZE` and `MDTM`, over the control connection) for the part after the last one listed, continuing until a part is missing, and for the file and its parts when the listing shows none of them. It does not cover what `ListAsync` returns, which is the listing as received (and reused for `ListingCacheDuration`), nor a part missing from the middle of a partial listing, which is reported as a missing part (`FileNotFoundException`) rather than looked up.

To see each failed attempt as it happens, set `OnRetry` and log its `Operation`, `RemotePath` and `Exception.Message`. Receiving the server's greeting (`220`) on port 21 shows that the control connection can be opened; it does not show that the passive data ports can be reached.

### Who closes what

Methods that take a stream have a `leaveOpen` parameter (default `false`):

| You pass | `leaveOpen: false` (default) | `leaveOpen: true` |
|---|---|---|
| A path (`DbSusFile.OpenReader`, `DbfReader.Open(string)`, `DbcFile.OpenAsDbf(string)`, ...) | DbSus opens and closes the file; `leaveOpen` does not apply | — |
| A stream (`DbfReader.Open(Stream)`, `DbcFile.OpenAsDbf(Stream)`, `DbSusFile.OpenDbcReader(Stream)`) | Disposed with the returned reader or stream, **and also if the call throws** | Never disposed: it stays yours |
| Several streams, the parts of a table (`DbSusFile.OpenDbcReader(IEnumerable<DbcPart>)`, each part a stream and an optional name) | Each is disposed once its part has been read or the reader is disposed; **all of them if the call throws** | Never disposed |
| Two streams to `DbcFile.ConvertToDbf(Stream, Stream)` | Never disposed (there is no `leaveOpen`) | — |

So `using var reader = DbfReader.Open(File.OpenRead(path));` does not leak the file even when the header is invalid.

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.
- The exception is a plain `InvalidDataException`, like other data errors. To react **specifically** to a checksum failure (for example to discard a cached copy), set `DbcReadOptions.ChecksumMismatch`: it is called with a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32) just before the exception, and only for checksum failures.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

## Allocation-free reading

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record, nor does `GetChars` into a reused buffer when the encoding is single-byte (every code page chosen from the LDID). `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

### Reading without allocation

`GetValues` boxes every value. To read every field without allocating, pick a typed getter per field once, from the schema, and dispatch on it for each record. This requires knowing each field's type; here it comes from `GetFieldType`, which also reflects any conversion you configured:

```csharp
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

`GetChars` decodes without allocating when the encoding is single-byte, which is the case for every code page chosen from the LDID. Measured with this exact loop over the CNES fixture repeated to 204,000 records, as a `.dbf` and as a `.dbc`, leaving out what opening the reader allocates. On .NET 10 (`GC.GetAllocatedBytesForCurrentThread`): 0 bytes for the `.dbf` and 4,120 bytes for the `.dbc`, a single 4 KB buffer at the end of the part to read the remaining data and verify the checksum, and no garbage collection. On .NET Framework 4.8 (`AppDomain.MonitoringTotalAllocatedMemorySize`, a counter that advances by allocation blocks rather than per object, so it cannot show that 4 KB buffer): 0 bytes in both cases and no garbage collection. Codes stored as ASCII digits can also be compared without decoding at all, on the raw bytes: `reader.GetRawBytes(i).StartsWith("0301"u8)`. `u8` literals need C# 11 or later in your project (on .NET Framework, set `<LangVersion>` accordingly).

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

In a real-world test on the same machine (Windows 11, .NET SDK 10.0.401), a complete SIA-PA month split into two parts, about 8 million records, was read in about 11 seconds, with about 0 bytes allocated per record and no garbage collection. The memory peak was 34 MiB: the peak working set (`PeakWorkingSet64`) of the whole process, the .NET runtime included.

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
