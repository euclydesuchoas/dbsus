# DbSus

A .NET library for reading the `.dbc` (compressed dBase) and `.dbf` files distributed by DATASUS, in fully managed C#, with no dependency on R, Python or native binaries.

> **Disclaimer:** DbSus is an independent project. It is not affiliated with, endorsed by or maintained by the Brazilian Ministry of Health (Ministério da Saúde) or DATASUS.

## Status

Early development. Nothing is published on NuGet yet and the API is not stable.

## Planned packages

| Package | Purpose |
|---|---|
| `DbSus` | Core: DBC decompression and DBF reading (streaming, typed getters, `DbDataReader`). |
| `DbSus.Client` | Direct access to the DATASUS file server, chaining download, decompression and reading. |

Target frameworks: `netstandard2.0` and `net10.0`.

## Allocation-free reading

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record. `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.
- The exception is a plain `InvalidDataException`, like other data errors. To react **specifically** to a checksum failure (for example to discard a cached copy), set `DbcReadOptions.ChecksumMismatch`: it is called with the part index and name just before the exception, and only for checksum failures.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

```csharp
using var reader = DbSusFile.OpenDbcReader(DbSusFile.FindParts("PAMG2407a.dbc"));
using var transaction = connection.BeginTransaction();
using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "staging_pa" };
bulk.WriteToServer(reader);   // throws if any part's checksum does not match
transaction.Commit();         // reached only when every part was read and verified
```

## Downloading from DATASUS (`DbSus.Client`)

```csharp
var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = @"D:\datasus-cache" });

// Every part of SIA-PA MG 2024-07 (the FTP listing says how many), downloaded once, then read as one table.
using var reader = await client.OpenReaderAsync("/SIASUS/200801_/Dados/PAMG2407.dbc");
```

- Paths are relative to `/dissemin/publicos` (configurable). `ListAsync`, `GetInfoAsync`, `FindPartsAsync`, `ReadHeaderAsync` (schema and record count from the first few kilobytes) and `DownloadAsync` are also available.
- Downloads resume after a dropped connection, restart from zero if the file changed on the server, retry transient failures with backoff, and are verified (CRC-32) before being used or cached.
- The client is thread-safe and meant to be shared (e.g. a singleton). It opens at most two connections by default (four at most), to be gentle with the server; the limit applies per instance.
- The cache is optional. Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy.
- Each entry has a small `entry.lock` file used to coordinate threads and processes. Pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it); `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, and is meant for when no other process is downloading into the same cache.

### Security note

The DATASUS server only offers **plain FTP** (it refuses TLS), so traffic is unencrypted and could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

## License

[MIT](LICENSE).
