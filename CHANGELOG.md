# Changelog

All notable changes to this project are documented in this file. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/) (during 0.x, a minor version may break the API).

## [Unreleased]

### Added

#### DbSus

- DBC decompression to DBF (`DbcFile.OpenAsDbf`, `DbcFile.ConvertToDbf`), with the CRC-32 check (`DbcReadOptions`).
- `DbcReadOptions.ChecksumMismatch`, a callback to react specifically to a checksum failure; it is called with a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32) just before the exception.
- `DbfReader`, a streaming `DbDataReader`: typed getters, raw bytes (`GetRawBytes`), field projection (`DbfReadOptions.Columns`), deleted records (`IncludeDeletedRecords`), blank strings as null (`BlankStringsAsNull`), and the encoding taken from the header's language driver ID, with a fallback (`FallbackEncoding`).
- `DbfReader.EncodingSource` tells where the encoding came from: `DbfReadOptions.Encoding`, the header's language driver ID, or the fallback.
- Conversions of character fields (`DbfFieldConversion`): `Int32`, `Int64`, `Decimal`, and `Date` with the formats `yyyyMMdd`, `ddMMyyyy` and `yyyyMM`.
- Tables split into parts, read as one table (`DbSusFile.FindParts`, `OpenReader`, `OpenDbcReader`, `DbcPart`).

#### DbSus.Client

- `DatasusClient` for the DATASUS FTP server: listing, file size and date, discovery of the parts of a split table, header reading without a full download, resumable downloads checked with the DBC's CRC-32, an optional cache, and a reader over remote files (`OpenReaderAsync`).
- Retries of transient failures with exponential backoff (`MaxRetries`, `InitialRetryDelay`, `OnRetry`), and typed exceptions (`DatasusTransferException`, `DatasusRetriesExhaustedException`, `DatasusRequestRefusedException`, `InsufficientDiskSpaceException`). An exception that FluentFTP is not meant to throw (such as the `NullReferenceException` of FluentFTP 55.0.0 in a listing whose control connection the server has closed without notice) is retried as a transient failure, on another connection. A listing that the server refuses with a permanent reply (FTP 5xx, 550 included) is a `DatasusRequestRefusedException` and is not retried. The folder of preliminary data of SIM and SINASC is optional: a 550 to its listing is taken as there being no such folder.
- Read timeouts and cancellation (verified on .NET 10 and .NET Framework 4.8).
- On .NET Framework, a `NotSupportedException` when the FluentFTP version in use does not report its connections to the client.
- A catalog of 42 DATASUS file types, from SIASUS, SIHSUS, CNES, SIM, SINASC, CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE and RESP (`DatasusCatalog`, `DatasusDataset`, `DatasusUf`, `DatasusPeriod`): paths built from a dataset, a state and a period (`GetPath`, `GetCandidatePaths`), and tables found (`FindTableAsync`), listed (`ListTablesAsync`) and opened (`OpenReaderAsync` by dataset, state and period, or from a `DatasusTableInfo`) on the server. `OpenReaderAsync` by dataset, state and period opens final data only; preliminary data is opened from a `DatasusTableInfo`, whose `IsPreliminary` says so.
- The file for the whole country of SIM-DO, SINASC-DN, PCE and RESP: `DatasusDataset.GetNationalPath` and `GetNationalCandidatePaths` build its path, and `DatasusClient.FindNationalTableAsync`, `ListNationalTablesAsync` and `OpenNationalReaderAsync` (final data only) find, list and open it. Such a table has no state (`DatasusTableInfo.Uf` is `null`), and the methods by state never return it.
- For SIASUS-PA, SIHSUS RD, SP and RJ, SIM-DO and SINASC-DN, the catalog also builds the folder layout that came before the one in use now (from 1994-07, 1992-01, 1979 and 1994): a period gets the folder and the file name of its layout, and `ListTablesAsync` over a range that crosses layouts lists the folder of each.
- For a dataset whose file names carry two digits of the year (every monthly one, and PCE and RESP), the catalog builds paths up to ninety-nine years after the start of the folder layout in use now; a later period, whose two-digit year would give it the name of an earlier one, fails with `ArgumentOutOfRangeException`.

### Notes

- Target frameworks: `net10.0`, `net472` and `netstandard2.0`.
- `DbSus.Client` requires FluentFTP 55.0.0 or later and is tested with FluentFTP 55.0.0; on .NET Framework, other versions may need a binding redirect; SDK-style executables generate it automatically.

### Known limitations

- The handling of stalled reads was verified on .NET 10 and .NET Framework 4.8; .NET 8 and 9, Mono, Unity and Xamarin were not checked.
- On .NET Framework and .NET 8, `GetFieldValue<T>` with a nullable type (e.g. `GetFieldValue<int?>`) allocates on every call: about 24 bytes on .NET Framework 4.8.1, and 24 bytes (32 for `decimal?`) on .NET 8. The non-nullable forms do not, nor do the nullable forms on .NET 9 and .NET 10 (measured).
- On .NET Framework without long path support, a deep `CacheDirectory` can make a cache entry's path longer than 260 characters; the download then fails with an `IOException` before the file's data is requested. Keep `CacheDirectory` short (see the compatibility guide). The failure was not observed on .NET 10.
- A listing can be partial if the client's read of it fails while the server still answers `226`. `FindPartsAsync` reduces the risk for split tables but does not remove it; `ListAsync` and `ListTablesAsync` return the listing as received, so a partial one omits files without any error. `FindTableAsync` asks the server for the file itself.
- The catalog covers 42 file types, by state, and the file for the whole country of four of them: the folder layout that DATASUS uses now and, for six of them, the one before it. The file types that only have a file for the whole country (SINAN, for one) and SIASUS-BI are reached with paths as strings (see the DATASUS paths guide). It checks the shape of a request, never whether the file exists. One real file of each of the 42 file types was read with DbSus, usually a small and old one, and one of each of the six older layouts; of the files for the whole country, one of PCE and one of RESP were read, and those of SIM-DO and SINASC-DN had only headers read; layouts can differ in other periods.
