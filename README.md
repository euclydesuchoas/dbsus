# DbSus

[![DbSus on NuGet](https://img.shields.io/nuget/vpre/DbSus?label=DbSus)](https://www.nuget.org/packages/DbSus)
[![DbSus.Client on NuGet](https://img.shields.io/nuget/vpre/DbSus.Client?label=DbSus.Client)](https://www.nuget.org/packages/DbSus.Client)
[![CI](https://img.shields.io/github/actions/workflow/status/euclydesuchoas/dbsus/ci.yml?branch=main&label=CI)](https://github.com/euclydesuchoas/dbsus/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE)

A .NET library for reading the `.dbc` (compressed dBase) and `.dbf` files distributed by DATASUS, in fully managed C#, with no dependency on R, Python or native binaries. DATASUS is the Ministry of Health's Department of Information and Informatics of the SUS, Brazil's public health system. It distributes data files from its health information systems, many of them as `.dbc`.

> **Disclaimer:** DbSus is an independent project. It is not affiliated with, endorsed by or maintained by the Brazilian Ministry of Health (Ministério da Saúde) or DATASUS.

Preview: the API may change between 0.x versions. See the [changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).

## Install

```shell
dotnet add package DbSus --prerelease
dotnet add package DbSus.Client --prerelease
```

| Package | Use it to |
|---|---|
| `DbSus` | Read `.dbc` and `.dbf` files you already have: decompression while you read, a `DbDataReader`, tables split into parts. |
| `DbSus.Client` | Download from the DATASUS FTP server, find tables by dataset, state and period, and read them. It depends on `DbSus`. |

Both packages run on .NET 8 or later and on .NET Framework 4.7.2 or later, including .NET 8 and .NET 9, whose support by Microsoft ended in November 2026. See [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md).

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

Download from DATASUS and read a table named by dataset, state and period, and let the client find its file or parts (112 file types, listed in the [paths guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md)):

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

Reading by path, without a download, and everything else the client does are in the [client guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md); everything the reader does is in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).

## Limitations

- The CRC-32 of a `.dbc` can only be checked once the last byte has been decompressed, so a mismatch is reported at the end of the data, as an `InvalidDataException`. Records returned before that cannot be trusted either: when loading into a database, commit only after `Read()` has returned `false` without an exception (see "Integrity is confirmed only at the end" in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md)).
- Character encoding: the code page comes from the header's language driver ID (LDID), and LDID 0x00 (no driver) is read as code page 1252; the evidence for some LDIDs is thin. When the text matters, check it (see "Character encoding" in the [reading guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md)).
- Tested with dBase III files. Field types `C`, `N`, `F`, `D` and `L` are interpreted; others are exposed only as their stored bytes. DbSus reads; it does not write `.dbc` files.
- The reader is forward-only and not thread-safe. `DatasusClient` is thread-safe.
- A listing from the server can be partial if the client's read of it fails while the server still answers `226` (see "A known limit of listings" in [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md)).
- Reading a table without a download (`OpenStreamingReaderAsync`) is opt-in and in preview: its API and its behavior may change before a stable release.

The other limitations are in the guides and in the [changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).

## Security

The DATASUS server lists `AUTH TLS` in its `FEAT` reply but refuses it: tried in October 2026, it answered `534 Local policy on server does not allow TLS secure connections.` DbSus therefore connects **without encryption** (plain FTP) and logs in anonymously, so no account credentials are exposed, but traffic could be observed or altered in transit. DbSus checks the size reported by the server and the **CRC-32** stored in each `.dbc`. A CRC-32 detects accidental corruption, **not deliberate tampering**: whoever alters a file can recompute it. The data is public, but if your use requires authenticity, compare against hashes obtained through a trusted channel.

A `.dbc` can decompress to about 170 to 190 times its size. For files that do not come from DATASUS, read through a stream (`DbcFile.OpenAsDbf`) and stop at a limit of your own, because `DbcFile.ConvertToDbf` writes all of the output.

Keep the cache folder writable only by you. What `DbSus.Client` checks of what the server sends, and what plain FTP leaves open, are under "Behavior" in the [client guide](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md).

To report a vulnerability of DbSus itself, in private, see the [security policy](https://github.com/euclydesuchoas/dbsus/blob/main/SECURITY.md).

## License

[MIT](https://github.com/euclydesuchoas/dbsus/blob/main/LICENSE). The DBC decompressor is a C# port of Mark Adler's `blast.c` (zlib license); see the [third-party notices](https://github.com/euclydesuchoas/dbsus/blob/main/THIRD-PARTY-NOTICES.md). The package icon is an original design of this project and is covered by the same MIT license.

## More

- Guides: [reading](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md), [client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md), [DATASUS paths and file names](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md), [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md), [performance](https://github.com/euclydesuchoas/dbsus/blob/main/docs/performance.md), [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md), [changelog](https://github.com/euclydesuchoas/dbsus/blob/main/CHANGELOG.md).
- Questions: open a discussion, <https://github.com/euclydesuchoas/dbsus/discussions>. To report a problem or to comment on how you use DbSus, open an issue in the repository on GitHub (the **Issues** tab has a form for each). Do not attach a file of DATASUS and do not paste the content of records, which can hold personal data: give the path of the file on the server. To change the code, see [contributing](https://github.com/euclydesuchoas/dbsus/blob/main/CONTRIBUTING.md).
