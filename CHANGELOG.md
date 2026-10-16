# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/) (during 0.x, a minor version may break the API).

## [Unreleased]

### Changed

- The CRC-32 check of a `.dbc` processes eight bytes at a time: a full decompression takes about 26% less time on .NET 10 and about 24% less on .NET Framework 4.8.1 (measured).
- With `DbcReadOptions.VerifyChecksum` set to `false`, the CRC-32 is no longer computed, which takes about 14% to 16% less time for a full decompression (measured).
- Huffman codes in a `.dbc` are decoded through lookup tables instead of one bit at a time: a full decompression takes about 31% to 34% less time on .NET 10 and about 27% to 30% less on .NET Framework 4.8.1 (measured).
- Copies in a `.dbc` are written as blocks straight into the caller's buffer instead of one byte at a time: together with the lookup tables, a full decompression takes about 48% to 50% less time on .NET 10 and about 35% to 39% less on .NET Framework 4.8.1 than with the faster CRC-32 alone (measured).

## [0.1.0-preview.1] - 2026-10-14

### Added

#### DbSus

- DBC decompression to DBF (`DbcFile.OpenAsDbf`, `DbcFile.ConvertToDbf`), with the CRC-32 check (`DbcReadOptions`).
- `DbcReadOptions.ChecksumMismatch`, a callback to react specifically to a checksum failure; it is called with a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32) just before the exception.
- `DbfReader`, a streaming `DbDataReader`: typed getters, raw bytes (`GetRawBytes`), field projection (`DbfReadOptions.Columns`), deleted records (`IncludeDeletedRecords`), blank strings as null (`BlankStringsAsNull`), and the encoding taken from the header's language driver ID, with a fallback (`FallbackEncoding`).
- `DbfReader.EncodingSource` tells where the encoding came from: `DbfReadOptions.Encoding`, the header's language driver ID, or the fallback.
- `DbfReadOptions.UnconvertibleValues` (`DbfUnconvertibleValueHandling`): what the reader does with the content of an `N`, `F` or `D` field that is not a number or a date. `Throw`, the default, is a `FormatException`, whose message points at the option and never has the content; `AsterisksAsNull` reads as null a field filled with asterisks; `Null` reads as null whatever does not convert. It does not apply to `L` fields nor to converted `C` fields.
- Conversions of character fields (`DbfFieldConversion`): `Int32`, `Int64`, `Decimal`, and `Date` with the formats `yyyyMMdd`, `ddMMyyyy` and `yyyyMM`.
- Tables split into parts, read as one table (`DbSusFile.FindParts`, `OpenReader`, `OpenDbcReader`, `DbcPart`).

#### DbSus.Client

- `DatasusClient` for the DATASUS FTP server: listing, file size and date, discovery of the parts of a split table, header reading without a full download, resumable downloads checked with the DBC's CRC-32, an optional cache, and a reader over remote files (`OpenReaderAsync`).
- Retries of transient failures with exponential backoff (`MaxRetries`, `InitialRetryDelay`, `OnRetry`), and typed exceptions (`DatasusTransferException`, `DatasusRetriesExhaustedException`, `DatasusRequestRefusedException`, `InsufficientDiskSpaceException`).
- Read timeouts and cancellation (verified on .NET 10 and .NET Framework 4.8.1).
- On .NET Framework, a `NotSupportedException` when the FluentFTP version in use does not report its connections to the client.
- A catalog of 112 DATASUS file types, from SIASUS, SIHSUS, CNES, SIM, SINASC, CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE, RESP, ESUSNOTIFICA, PO and SINAN (`DatasusCatalog`, `DatasusDataset`, `DatasusUf`, `DatasusPeriod`), to build paths (`GetPath`, `GetCandidatePaths`) and to find, list and open tables by dataset, state and period (`FindTableAsync`, `ListTablesAsync`, `OpenReaderAsync`).
- The file for the whole country, which SIM-DO, SINASC-DN, PCE and RESP have beside the files of the states and 69 file types have alone, the 58 of SINAN among them (`GetNationalPath`, `GetNationalCandidatePaths`, `FindNationalTableAsync`, `ListNationalTablesAsync`, `OpenNationalReaderAsync`); `DatasusDataset.Coverage` says which files a file type has.
- The folder layout that came before the one in use now, for nine file types: SIASUS-PA, SIHSUS RD, SP and RJ, SIM DO, DOEXT, DOFET and DOINF, and SINASC-DN.
- The numbered parts of SIASUS-BI (`BIMG2312_1.dbc`, `BIMG2312_2.dbc`), read through the catalog as one table.
- Paths up to ninety-nine years after the start of the folder layout in use now, for file names with two digits of the year; a later period fails with `ArgumentOutOfRangeException`.

### Notes

- Target frameworks: `net10.0`, `net472` and `netstandard2.0`.
- `DbSus.Client` requires FluentFTP 55.0.0 or later and is tested with FluentFTP 55.0.0; on .NET Framework, other versions may need a binding redirect; SDK-style executables generate it automatically.

### Known limitations

- The handling of stalled reads was verified on .NET 10 and .NET Framework 4.8.1; .NET 8 and 9, Mono, Unity and Xamarin were not checked.
- On .NET Framework and .NET 8, `GetFieldValue<T>` with a nullable type (e.g. `GetFieldValue<int?>`) allocates on every call: about 24 bytes on .NET Framework 4.8.1, and 24 bytes (32 for `decimal?`) on .NET 8. The non-nullable forms do not, nor do the nullable forms on .NET 9 and .NET 10 (measured).
- On .NET Framework without long path support, a deep `CacheDirectory` can make a cache entry's path longer than 260 characters; the download then fails with an `IOException` before the file's data is requested. Keep `CacheDirectory` short (see the compatibility guide). The failure was not observed on .NET 10.
- A listing can be partial if the client's read of it fails while the server still answers `226`. `FindPartsAsync` reduces the risk for split tables but does not remove it; `ListAsync` and `ListTablesAsync` return the listing as received, so a partial one omits files without any error. `FindTableAsync` asks the server for the file itself.
- The catalog covers 112 file types: 43 by state, four of which also have a file for the whole country, and 69 with a file for the whole country only; it knows the folder layout that DATASUS uses now and, for nine of them, the one before it. It checks the shape of a request, never whether the file exists, and the files outside it are reached with paths as strings (see the DATASUS paths guide).
- One real file with records of 111 of the 112 file types was read with DbSus, usually a small and old one, and one of each of the nine older layouts. Not read: any file of SINAN-INFL, because no file of it was found on the server (the catalog has it, with the folders and the file name of the other SINAN file types, because the file transfer page of the DATASUS website lists it); the records of the files for the whole country of SIM-DO and SINASC-DN, of which only headers were read (those of PCE and RESP were read); and a complete set of numbered parts of SIASUS-BI. Layouts can differ in other periods.
- In twelve SINAN files read, some numeric and date fields did not convert (see `DbfReadOptions.UnconvertibleValues`).
