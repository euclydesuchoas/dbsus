# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/) (during 0.x, a minor version may break the API).

## [Unreleased]

### Added

- `DbSus.Client` reads a table straight from the server, without a download, with `OpenStreamingReaderAsync`, in preview (its behavior may change before a stable release). It takes a path, of any part or of the unsplit name, whose folder it lists to find the other parts, or a `DatasusTableInfo`, whose parts it reads as they are, and returns a `DbfReader` over every part of the table, in order. Nothing is written to disk: the cache and the temporary folder are not used, and `VerifyOnDownload` does not apply.
- Each reader from `OpenStreamingReaderAsync` holds one of the client's connections for its whole life, and at most `MaxConnections` − 1 readers are open at a time; one more, or any with `MaxConnections` = 1, fails at once with `InvalidOperationException`. `Read` blocks while it waits for the server, the cancellation token holds for the life of the reader, and each part's CRC-32 is checked at the end of that part, so the records are confirmed only when `Read` returns `false`. A transfer that drops or stalls resumes on a new connection with the usual retries and `OnRetry`; a part that changes on the server while it is read makes `Read` throw the new `DatasusRemoteFileChangedException`, with both versions. See "Reading without a download" in the client guide.

### Changed

- `DbSus.Client` gives the opening of a transfer that resumes an interrupted download, from a byte beyond the start, a limit of its own: 5 seconds from the passive-mode request to the server's reply to the transfer command. Past it the connection is closed and the attempt counts as a transient failure, retried on a new connection with the usual backoff and `OnRetry`. Before, such an opening could wait as long as `ReadTimeout`, or until the operating system gave up on an unanswered command. Opening a transfer from the start keeps `ReadTimeout` alone.
- An interrupted download still resumes from where it stopped, and so does a partial download left by an earlier call or another process sharing the cache. When an attempt fails to open such a resumption, the next attempt of the same call starts over from the beginning of the file, without resuming, if at most 16 MiB had been received or if it is the last attempt that `MaxRetries` allows; otherwise it tries to resume again. A failure in the middle of a transfer that opened never makes the download start over. With `MaxRetries` set to 0, a resumption that fails to open is thrown as any other failure, and the partial download is kept.

## [0.1.0-preview.4] - 2026-10-21

### Fixed

- `DbSus.Client` declares the upper bound of its `DbSus` dependency as `0.2.0-a` instead of `0.2.0-0`, which nuget.org refuses because the label has only digits. For this version the range is `[0.1.0-preview.4, 0.2.0-a)`; it still leaves out `0.2.0` and its previews.

### Notes

- `DbSus` 0.1.0-preview.4 has the same code as 0.1.0-preview.3; it is published so that both packages carry the same version.

## [0.1.0-preview.3] - 2026-10-21

`DbSus` 0.1.0-preview.3 was published. `DbSus.Client` 0.1.0-preview.3 was not: nuget.org rejected its dependency range (see 0.1.0-preview.4), so its changes are in 0.1.0-preview.4.

### Added

- The `net10.0` build of `DbSus` is marked as compatible with trimming and Native AOT; `DbSus.Client` and the other builds of `DbSus` (`netstandard2.0`, `net472`) are not marked. How it was verified: a CI job publishes an application that uses it trimmed (self-contained, for Linux x64 and Windows x64) and with Native AOT (Windows x64), where any warning of the trimmer or of the Native AOT compiler fails the job; it runs each published application on data the program builds itself and produces the same output as a normal build. Native AOT was not tested on other platforms, nor trimming on other architectures, and `DataTable.Load` is not compatible with trimming because of `System.Data`.

### Changed

- `DbSus.Client` declares `DbSus` from its own version up to the next minor version, which it excludes together with its previews (for this version, `[0.1.0-preview.3, 0.2.0-0)`), instead of that version or any later one; NuGet warns (NU1608) when it is combined with a later `DbSus`.
- `DbSus.Client` opens each data connection with `PASV`, ignores the address in the reply and connects to `Host` (FluentFTP's `PASVEX` mode); it used to send `EPSV` when the server listed it and otherwise follow the address the server named. Over IPv6, FluentFTP sends `EPSV` in its place. The client no longer depends on FluentFTP's `AutoPassive` fallback, which FluentFTP 55.1.0 changed. A server that sends its data from an address other than `Host` is no longer supported.
- Without a cache, `DbSus.Client` puts each download in a folder of its own, `DbSus-<random id>`, directly in the system temporary directory, and removes it when the reader is disposed; earlier versions used a shared `DbSus/downloads`. On the `net10.0` build outside Windows the folder is readable by its owner only; the `netstandard2.0` build (the one applications on .NET 8 and 9 get) and the `net472` build keep the system's default permissions (usually 0755 on Linux). Folders left behind are cleaned up after 24 hours, without delaying a download.

### Fixed

- The messages that `DbSus.Client` builds from a server reply show its control, format and line separator characters escaped (`\xNN`, `\uXXXX`) and keep at most 512 characters of it; exceptions raised by FluentFTP, kept as inner exceptions, are not changed.
- A size that the disk cannot hold, announced by the server, fails with `InsufficientDiskSpaceException` before the folder of the cache entry is created, so no empty folder is left behind.

## [0.1.0-preview.2] - 2026-10-16

### Changed

- When reading a `.dbc`, a full decompression takes about 64% to 67% less time than in 0.1.0-preview.1 on .NET 10, about 65% less on .NET 8 and about 53% to 55% less on .NET Framework 4.8.1 (measured): the CRC-32 is computed eight bytes at a time, Huffman codes are decoded through lookup tables, and copies are written as blocks straight into the caller's buffer. The output and the checks are the same, and the API has not changed.
- With `DbcReadOptions.VerifyChecksum` set to `false`, the CRC-32 is no longer computed, which takes about 29% to 31% less time for a full decompression on .NET 10 and .NET 8, and about 22% to 25% less on .NET Framework 4.8.1 (measured); a file whose data does not match its CRC-32 is read without an error, as before. Nothing changes with the default (`true`).
- The release notes of the packages point to this changelog.

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
