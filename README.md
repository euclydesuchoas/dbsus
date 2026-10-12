# DbSus

A .NET library for reading the `.dbc` (compressed dBase) and `.dbf` files distributed by DATASUS, in fully managed C#, with no dependency on R, Python or native binaries. DATASUS is the Ministry of Health's Department of Information and Informatics of the SUS, Brazil's public health system. It distributes data files from its health information systems, many of them as `.dbc`.

> **Disclaimer:** DbSus is an independent project. It is not affiliated with, endorsed by or maintained by the Brazilian Ministry of Health (Ministério da Saúde) or DATASUS.

## Status

Preview: the API may change between 0.x versions. See the [changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).

## Install

```shell
dotnet add package DbSus --prerelease
dotnet add package DbSus.Client --prerelease
```

| Package | Use it to |
|---|---|
| `DbSus` | Read `.dbc` and `.dbf` files you already have: decompression, a streaming `DbDataReader`, tables split into parts. |
| `DbSus.Client` | Download from the DATASUS FTP server and read what you download. It depends on `DbSus`. |

## Quick start

The snippets are statements: paste them into a method (the last one into an async method) or into a top-level program (C# 9 or later).

Read a `.dbc` (or a `.dbf`):

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

Convert a `.dbc` into a `.dbf`:

```csharp
using DbSus.Dbc;

DbcFile.ConvertToDbf("LTRR0610.dbc", "LTRR0610.dbf");   // all or nothing: no partial file on failure
```

Download from DATASUS and read:

```csharp
using System;
using DbSus.Client;

// Without CacheDirectory, downloads go to a folder under the system's temporary directory.
await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });
using var reader = await client.OpenReaderAsync("/CNES/200508_/Dados/LT/LTRR0610.dbc");
int cnes = reader.GetOrdinal("CNES");
while (reader.Read())
{
    Console.WriteLine(reader.GetString(cnes));
}
```

Or name the table by dataset, state and period, and let the client find its file or parts (33 file types, listed in the [paths guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md)):

```csharp
using System;
using DbSus.Client;

await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });
using var reader = await client.OpenReaderAsync(DatasusCatalog.Cnes.LT, DatasusUf.RR, DatasusPeriod.OfMonth(2006, 10));
int cnes = reader.GetOrdinal("CNES");
while (reader.Read())
{
    Console.WriteLine(reader.GetString(cnes));
}
```

## Features

- DBC decompressed while you read: no temporary file, no full copy in memory.
- A `DbDataReader`, so it plugs into ADO.NET consumers such as `DataTable.Load`.
- Typed getters that do not allocate per record (measured on .NET 8, 9 and 10 and on .NET Framework 4.8), and raw access to the stored bytes.
- Field projection (`Columns`), deleted records on request, blank strings as null.
- Encoding taken from the header's language driver ID, with a fallback.
- Opt-in conversions of character fields: `Int32`, `Int64`, `Decimal`, and dates (`yyyyMMdd`, `ddMMyyyy`, `yyyyMM`).
- Tables split into parts read as one table, from paths or streams.
- The CRC-32 stored in each `.dbc` is verified.
- `DbSus.Client`: listing, file size and date, split-table discovery, headers without a full download, resumable and verified downloads, an optional cache shared safely between threads and processes, retries with backoff, typed exceptions, read timeouts and cancellation.
- `DatasusCatalog`: 33 DATASUS file types by name, to build paths and to find, list and open tables by dataset, state and period.

## Guides

- [Reading DBC and DBF files](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md): value types, who closes what, split tables, conversions, encodings, ADO.NET, integrity, reading without allocation.
- [Downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md): paths, retries, downloads, the cache, exceptions.
- [DATASUS paths and file names](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md): the 33 file types of the catalog, their folders and file names, finding and opening tables, what is outside the catalog.
- [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md): failed or stalled connections, worst-case times, a known limit of listings.
- [Performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md): throughput, field projection, allocation.
- [Compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md): target frameworks, runtimes, dependencies, FluentFTP versions.
- [Changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).

## Integrity

The CRC-32 of a `.dbc` can only be checked once the last byte has been decompressed, so a mismatch is reported at the end of the data, as an `InvalidDataException`. Records returned before that cannot be trusted either. When loading into a database, write inside a transaction or into a staging table, and commit only after `Read()` has returned `false` without an exception. Details are in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).

## Limitations

- DbSus reads; it does not write `.dbc` files. It writes `.dbf` files only as the output of a conversion.
- Tested with dBase III files (version 0x03). One DATASUS file with another version byte (0x30, a SIASUS-ABO file) was read without error; nothing else is known about files of that version. Field types `C`, `N`, `F`, `D` and `L` are interpreted; others, such as `M` (memo), are exposed only as their stored bytes, and memo files are not read.
- Character encoding: the code page comes from the header's language driver ID (LDID), and LDID 0x00 (no driver) is read as code page 1252. The only accented text found (CNES) reads correctly as code page 1252, including in a file whose LDID is 0x00; the 1252 fallback for LDID 0x00, and whether files marked 0x02 and 0x57 really hold text in those code pages, are still to be validated. `DbfReadOptions.Encoding` overrides the code page, and `DbfReader.EncodingSource` tells where the encoding of a reader came from: the option, the LDID, or the fallback.
- The reader is forward-only. It is not thread-safe, and the asynchronous methods it inherits from `DbDataReader` complete synchronously. `DatasusClient` is thread-safe.
- On .NET Framework and .NET 8, `GetFieldValue<T>` with a nullable type (e.g. `GetFieldValue<int?>`) allocates on every call: about 24 bytes on .NET Framework 4.8.1, and 24 bytes (32 for `decimal?`) on .NET 8. The non-nullable forms do not, and on .NET 9 and .NET 10 the nullable forms do not either (measured).
- `DatasusCatalog` covers 33 file types, by state: the folder layout that DATASUS uses now and, for six of them, the one before it. It checks the shape of a request (a month or a year, a real state), never whether the file exists; other files are reached with the path as a string.
- A listing from the server can be partial if the client's read of it fails while the server still answers `226`. `FindPartsAsync` reduces the risk for split tables but does not remove it; `ListAsync` returns the listing as received.
- The handling of stalled reads was verified on .NET 10 and .NET Framework 4.8; .NET 8 and 9, Mono, Unity and Xamarin were not checked.

## Security

The DATASUS server lists `AUTH TLS` in its `FEAT` reply but refuses it: tried in October 2026, it answered `534 Local policy on server does not allow TLS secure connections.` DbSus therefore connects **without encryption** (plain FTP), so traffic could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

## License

[MIT](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE). The DBC decompressor is a C# port of Mark Adler's `blast.c` (zlib license); see the [third-party notices](https://github.com/euclydesuchoas/dbsus/blob/main/THIRD-PARTY-NOTICES.md).
