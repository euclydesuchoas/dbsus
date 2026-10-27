# DbSus

[![DbSus on NuGet](https://img.shields.io/nuget/vpre/DbSus?label=DbSus)](https://www.nuget.org/packages/DbSus)
[![DbSus.Client on NuGet](https://img.shields.io/nuget/vpre/DbSus.Client?label=DbSus.Client)](https://www.nuget.org/packages/DbSus.Client)
[![CI](https://img.shields.io/github/actions/workflow/status/euclydesuchoas/dbsus/ci.yml?branch=main&label=CI)](https://github.com/euclydesuchoas/dbsus/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE)

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

Both packages target `net10.0`, `netstandard2.0` and `net472`: they run on .NET 8 or later and on .NET Framework 4.7.2 or later. They keep running on .NET 8 and .NET 9 after Microsoft's end of support for them (November 2026). See [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md).

## Quick start

The snippets are statements: paste them into a method (the last one into an async method) or into a top-level program (C# 9 or later). They are written in C# 8 (`using var`, `await using`): a .NET Framework project, whose default is C# 7.3, needs `<LangVersion>8.0</LangVersion>` in its project file.

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

Or name the table by dataset, state and period, and let the client find its file or parts (112 file types, listed in the [paths guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md)):

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
- Typed getters that do not allocate per record (measured on .NET 8, 9 and 10 and on .NET Framework 4.8.1), and raw access to the stored bytes.
- Field projection (`Columns`), deleted records on request, blank strings as null, and, on request, content of a numeric or date field that does not convert read as null (`UnconvertibleValues`).
- Encoding taken from the header's language driver ID, with a fallback.
- Opt-in conversions of character fields: `Int32`, `Int64`, `Decimal`, and dates (`yyyyMMdd`, `ddMMyyyy`, `yyyyMM`).
- Tables split into parts read as one table, from paths or streams.
- The CRC-32 stored in each `.dbc` is verified.
- `DbSus.Client`: listing, file size and date, split-table discovery, headers without a full download, resumable and verified downloads, an optional cache shared safely between threads and processes, retries with backoff, typed exceptions, read timeouts and cancellation.
- `DbSus.Client`, in preview: reading a table straight from the server, without a download (see "Reading without a download" in the [client guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md)).
- `DatasusCatalog`: 112 DATASUS file types by name, to build paths and to find, list and open tables by dataset, state and period, and the table for the whole country of the 73 that have one.

## Guides

- [Reading DBC and DBF files](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md): value types, who closes what, split tables, conversions, encodings, ADO.NET, integrity, reading without allocation.
- [Downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md): paths, retries, downloads, the cache, exceptions.
- [DATASUS paths and file names](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md): the 112 file types of the catalog, their folders and file names, finding and opening tables, what is outside the catalog.
- [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md): failed or stalled connections, worst-case times, a known limit of listings.
- [Performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md): throughput, field projection, allocation.
- [Compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md): target frameworks, runtimes, dependencies, FluentFTP versions.
- [Changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).

## Questions and problems

To ask a question or to report a problem, open an issue in the repository on GitHub (the **Issues** tab) and say the package, its version, the target framework and the operating system. Do not attach a file of DATASUS and do not paste the content of records, which can hold personal data: give the path of the file on the server. To report a vulnerability, see "Security" below; to change the code, see [contributing](https://github.com/euclydesuchoas/dbsus/blob/main/CONTRIBUTING.md).

## Integrity

The CRC-32 of a `.dbc` can only be checked once the last byte has been decompressed, so a mismatch is reported at the end of the data, as an `InvalidDataException`. Records returned before that cannot be trusted either. When loading into a database, write inside a transaction or into a staging table, and commit only after `Read()` has returned `false` without an exception. Details are in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).

## Limitations

- DbSus reads; it does not write `.dbc` files. It writes `.dbf` files only as the output of a conversion.
- Tested with dBase III files (version 0x03). One DATASUS file with another version byte (0x30, a SIASUS-ABO file) was read without error; nothing else is known about files of that version. Field types `C`, `N`, `F`, `D` and `L` are interpreted; others, such as `M` (memo), are exposed only as their stored bytes, and memo files are not read.
- Character encoding: the code page comes from the header's language driver ID (LDID), and LDID 0x00 (no driver) is read as code page 1252. In the files of the catalog whose bytes were counted in October 2026, few had text to judge by: it read as the LDID says for 0x02 and 0x03, for 0x00 the evidence is four bytes in one file, and for the other LDIDs seen there was none. In the DBF tables inside the auxiliary archives of the server, which are not file types of the catalog, the LDID did not tell the code page. When the text matters, check it. `DbfReadOptions.Encoding` overrides the code page, and `DbfReader.EncodingSource` tells where the encoding of a reader came from: the option, the LDID, or the fallback. The counts are under "Character encoding" in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).
- The reader is forward-only. It is not thread-safe, and the asynchronous methods it inherits from `DbDataReader` complete synchronously. `DatasusClient` is thread-safe.
- On .NET Framework and .NET 8, `GetFieldValue<T>` with a nullable type (e.g. `GetFieldValue<int?>`) allocates on every call: about 24 bytes on .NET Framework 4.8.1, and 24 bytes (32 for `decimal?`) on .NET 8. The non-nullable forms do not, and on .NET 9 and .NET 10 the nullable forms do not either (measured).
- `DatasusCatalog` covers 112 file types, in the folder layout that DATASUS uses now and, for nine of them, also in the one before it. It checks the shape of a request (a month or a year, a real state), never whether the file exists. One file type, SINAN-INFL, is in the catalog because the DATASUS website lists it, although no file of it was known on the server in October 2026. Other files are reached with the path as a string. The [paths guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md) says which file types have files by state, which have a file for the whole country, and what the catalog leaves out.
- A listing from the server can be partial if the client's read of it fails while the server still answers `226`. `FindPartsAsync` reduces the risk for split tables but does not remove it; `ListAsync` returns the listing as received.
- The handling of stalled reads was verified on .NET 10 and .NET Framework 4.8.1; .NET 8 and 9, Mono, Unity and Xamarin were not checked.
- On .NET Framework without long path support, a deep `CacheDirectory` can make the path of a cache entry longer than 260 characters, and the download then fails with an `IOException` before the data of the file is requested: keep `CacheDirectory` short (see "Long paths on .NET Framework" in [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)).

## Security

The DATASUS server lists `AUTH TLS` in its `FEAT` reply but refuses it: tried in October 2026, it answered `534 Local policy on server does not allow TLS secure connections.` DbSus therefore connects **without encryption** (plain FTP) and logs in anonymously, so no account credentials are exposed, but traffic could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

A `.dbc` can decompress to about 170 to 190 times its size. For files that do not come from DATASUS, read through a stream (`DbcFile.OpenAsDbf`) and stop at a limit of your own, because `DbcFile.ConvertToDbf` writes all of the output.

What `DbSus.Client` checks of what the server sends: every path it requests or writes, those built from the names of a listing included, passes the same checks as the paths you give it; the size (`SIZE`) and the time (`MDTM`) of a file must be well formed; a download stops at the size the server announced; and every data connection goes to `Host`, since the address in a `PASV` reply is ignored, so a server cannot send the client to another address. The messages that `DbSus.Client` builds from a server reply show its control characters escaped; exceptions raised by FluentFTP, kept as inner exceptions, are not changed.

What plain FTP leaves open: the CRC-32 of a `.dbc` detects corruption, not tampering, and a file that is not a `.dbc` is checked only for its size. Whoever can write to the cache folder can change what is in it, and a junction (tested on Windows) or a link inside it is followed when writing, so the folder should be writable only by you. The size of a listing has no limit.

To report a vulnerability of DbSus itself, in private, see the [security policy](https://github.com/euclydesuchoas/dbsus/blob/main/SECURITY.md).

## License

[MIT](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE). The DBC decompressor is a C# port of Mark Adler's `blast.c` (zlib license); see the [third-party notices](https://github.com/euclydesuchoas/dbsus/blob/main/THIRD-PARTY-NOTICES.md). The package icon is an original design of this project and is covered by the same MIT license.
