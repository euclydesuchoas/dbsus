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

### ADO.NET: `DataTable`, `SqlBulkCopy`, Dapper

`DbfReader` is a `DbDataReader`:

```csharp
using var reader = DbSusFile.OpenReader("LTRR0610.dbc");
var table = new DataTable();
table.Load(reader);
```

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

// Schema and record count from the first few kilobytes, without downloading the file.
var header = await client.ReadHeaderAsync("/CNES/200508_/Dados/LT/LTRR0610.dbc");

// Every part of SIA-PA MG 2024-07 (the FTP listing says how many), downloaded once, then read as one table.
using var reader = await client.OpenReaderAsync("/SIASUS/200801_/Dados/PAMG2407.dbc");
```

- Paths are relative to `/dissemin/publicos` (configurable). `ListAsync`, `GetInfoAsync`, `FindPartsAsync` and `DownloadAsync` are also available.
- Downloads resume after a dropped connection, restart from zero if the file changed on the server, retry transient failures with backoff, and are verified (CRC-32) before being used or cached.
- At most two connections by default (four at most), to be gentle with the server; the limit applies per instance.
- The cache is optional. Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy.
- Each entry has a small `entry.lock` file used to coordinate threads and processes. Pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it); `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, and is meant for when no other process is downloading into the same cache.

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.
- The exception is a plain `InvalidDataException`, like other data errors. To react **specifically** to a checksum failure (for example to discard a cached copy), set `DbcReadOptions.ChecksumMismatch`: it is called with the part index and name just before the exception, and only for checksum failures.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

## Allocation-free reading

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record. `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

## .NET Framework

The minimum is .NET Framework 4.7.2, which uses the packages' `net472` build. If an application still fails on the first read with a `FileLoadException` for `System.Memory` (for example an unusual build setup that resolves the `netstandard2.0` build instead), add this binding redirect to its `app.config`:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />
</dependentAssembly>
```

## Security note

The DATASUS server only offers **plain FTP** (it refuses TLS), so traffic is unencrypted and could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

## License

[MIT](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE). The DBC decompressor is a C# port of Mark Adler's `blast.c` (zlib license); see [THIRD-PARTY-NOTICES.md](https://github.com/euclydesuchoas/dbsus/blob/main/THIRD-PARTY-NOTICES.md).
