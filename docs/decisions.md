# DbSus — design decisions

Why the library is built the way it is, and what was learned about the files it reads. Entries that are not settled yet are marked **open** or **to validate**.

DbSus is an independent project, not affiliated with the Brazilian Ministry of Health or DATASUS.

## Scope

- Read the `.dbc` (compressed dBase) and `.dbf` files that DATASUS distributes, from .NET, with no external tools: no R, no Python, no native binaries, no P/Invoke.
- Two packages from one repository:
  - `DbSus`: DBC decompression and DBF reading (`DbSus.Dbc`, `DbSus.Dbf`, and the `DbSus` facade).
  - `DbSus.Client`: listing and downloading files from the DATASUS FTP server, with an optional local cache.
- `DbSus.Dbc` and `DbSus.Dbf` do not reference each other, not even in XML doc `cref`s; only the `DbSusFile` facade knows both. The package could be split later without breaking users.
- Not in scope for v1: writing DBC files, a source generator, a command-line tool.

## Architecture

- **Targets:** `netstandard2.0`, `net472` and `net10.0` for both packages. `net472` exists because .NET Framework applications using the `netstandard2.0` build hit a `System.Memory` binding conflict (`FileLoadException`, no automatic binding redirect); `net462` lacks `DbColumn`, so 4.7.2 is the minimum .NET Framework. There is no `net8.0` target: `netstandard2.0` covers it.
- **Dependencies:** none on `net10.0`. `System.Memory` on `net472`; `System.Memory` and `System.Text.Encoding.CodePages` on `netstandard2.0`. The client adds FluentFTP on every target and `Microsoft.Bcl.AsyncInterfaces` on `net472` and `netstandard2.0`. Only the client takes a third-party dependency.
- **Minimum dependency versions:** the packages declare the lowest versions that work (`System.Memory` 4.5.3, `System.Text.Encoding.CodePages` 4.4.0, `Microsoft.Bcl.AsyncInterfaces` 1.0.0), so an application that already references an older version does not hit a downgrade error (NU1605). `System.Memory` 4.5.0 and 4.5.1 fail on .NET Framework (`FileLoadException` for `System.Runtime.CompilerServices.Unsafe`). `src/Directory.Packages.props` lowers these versions for the libraries only; tests and benchmarks use current versions.
- **Encodings:** the library never calls the global `Encoding.RegisterProvider`; it uses `CodePagesEncodingProvider.Instance` locally (on .NET Framework, `Encoding.GetEncoding`), with no side effect on the host process.
- **Strong naming:** both assemblies are strong-named with the committed `DbSus.snk` (public key token `31b7992898fa311e`), following Microsoft's guidance for open-source libraries. The key never changes; a test pins the token.
- **Versioning:** `AssemblyVersion` is `Major.Minor.0.0` during 0.x (every 0.x minor may break) and major-only from 1.0. Releases stay 0.x/preview until the API settles.

## Origin of the code and licenses

- DbSus is MIT-licensed.
- The decompressor is a C# port of Mark Adler's `blast.c` (PKWare DCL "implode"), under the zlib license, from `madler/zlib` commit `767c4c947852e143f582c85f14cf573411df1b35` (`contrib/blast`, version 1.3). `THIRD-PARTY-NOTICES.md` keeps the original notice and marks the code as altered; it ships in the `DbSus` package.
- The DBC wrapper (header, checksum, start of the compressed stream) was determined and verified against real files.
- `read.dbc` (R) and `pyreaddbc` (Python) are AGPL-3. No code from read.dbc or pyreaddbc is included in DbSus; they are run only as external, black-box test oracles.

## The files

### DBC layout

Confirmed on every file examined:

- `[0, H)`: the DBF header, verbatim (`H` is the header length at offset 8, UInt16 little-endian).
- `[H, H + 4)`: CRC-32 (zlib/IEEE) of the **whole uncompressed DBF**, little-endian.
- `[H + 4, end)`: a PKWare DCL implode stream. Every DATASUS file seen uses uncoded literals and a 4 KiB dictionary. Nothing follows the end code.
- The DBF is the header plus the decompressed body. Its size is `H + records × record length`, plus a trailing 0x1A end-of-file marker in some sources (present in SIA-PA, absent in SIH-RD and CNES). Both are accepted, and bytes are preserved as stored.

### Checksum is the arbiter

- The stored CRC-32 decides whether a decompressed file is right, not an oracle. In one CNES file (`ST`, Roraima, 2023-01) the last header byte is 0x00 where a 0x0D field terminator is expected; DbSus keeps the byte and matches the stored CRC, while `pyreaddbc` 2.0.4 rewrites it to 0x0D and does not. Oracle hashes are checked against the stored CRC before being accepted.
- Because the CRC covers the whole DBF, a mismatch can only be reported after the last byte has been read.

### DBF dialect seen across sources

Twenty files across SIM, SINASC, SINAN, SIH, SIA and CNES were examined in aggregate (no individual values recorded). Here "examined" means their headers, and counts over their data, such as which field types and which bytes occur. It does not mean "read", which in these notes is reading a file record by record with the DbSus reader: that was done later, for one file of each of the 53 file types of the catalog (`docs/datasus-paths.md`), and found only `C` and `N` fields.

- **Header:** field descriptors end at 0x0D, at an empty name, or where the header length runs out (the CNES file above has no terminator at all). Spare bytes after the last field are tolerated; in every file examined the fields fill the record exactly.
- **Field types:** `C` everywhere, `N` in most sources, `D` only in SINAN; no `F`, `L` or `M`.
- **Numbers:** left-padded with spaces, decimal point `.`, digits after the point as declared. No negatives, no `*` overflow, no blank `N` values seen.
- **Dates in character fields** use `YYYYMMDD` in SIH, SIA and CNES but `DDMMYYYY` in SIM and SINASC; they cannot be inferred from field names.
- **Opaque bytes:** some identifier fields (in CNES professional and SIA APAC/BI/PS files) hold bytes above 0x7F that are not text in any code page, so raw access to field bytes is needed.
- **Deletion flag:** blank on every record examined.
- **File names:** some files use an upper-case extension (`.DBC`).

### Language driver (LDID) and code pages

- Values seen, in the headers of the files examined: 0x00 (no driver) in SIM, SINASC, SINAN, SIH SP/ER, SIA AM/AQ/PS and CNES SR/ST; 0x02 in SIH RD/RJ; 0x03 in SIA PA/BI; 0x57 in SIA AD/AR; 0x58 in CNES EQ/LT/PF/HB. The files read later, one of each file type of the catalog, had the same values, and added 0x00 in SIA ATD/ACF and 0x58 in CNES DC/EP/EF/GM/IN/RC. The seven read after them had 0x57 in SIA AB/AN and CIHA, 0x03 in SIA ABO, 0x00 in SIA SAD/AMP and 0x58 in CNES EE. The eleven file types for the whole country only, read in October 2026, had 0x00, except 0x35 in SIM-DOREXT, 0x03 in PO and 0x02 in SIHSUS-MT. Reading a file record by record does not check its text: for 0x57 (SIA AD and AR) and for CNES PF and HB the value says which code page is declared, and no text was looked at.
- Meaning: 0x02 = code page 850 and 0x03 = 1252 (Microsoft's Visual FoxPro table); 0x58 = 1252, 0x57 = ISO-8859-1 and 0x59 = 1252 (dBASE drivers, per the MiraMon documentation and GDAL's LDID table).
- 0x35 was seen in four SIM files: one of 2024, in a real-world test in early October 2026, whose record names neither the file nor its folder; the preliminary SIM-DO file of Roraima for 2026; and, in the folders of final data, the SIM-DO file of Roraima for 2020 and `DOREXT20.dbc`. It is in neither table, so the text is read with the fallback, code page 1252, as with 0x00. No text was looked at. In the two SIM-DO files of Roraima, of 2020 and of 2026, no field of any record has a byte above 0x7F, so there the code page makes no difference.
- The only accented text found (CNES) reads correctly as code page 1252, including in a file whose LDID is 0x00.
- **To validate:** the 1252 fallback for LDID 0x00, and whether files marked 0x02 and 0x57 really hold text in those code pages.

### Split tables (SIA-PA)

- Among the file types surveyed (the SIASUS, SIHSUS, CNES, SIM, SINASC and SINAN data folders listed on the server in October 2026), only SIA-PA is split into lettered parts, `a` to `d`. The headers of all 740 parts of the 307 split groups in `SIASUS/200801_/Dados` (fetched with range requests) were compared, and so were the two parts of one month held locally: no gaps, no group starting after `a`, never an unlettered file next to lettered parts, and all 434 part-to-part comparisons (433 among the files on the server, 1 between the local parts) identical (names, types, widths, decimals, record and header length, LDID, version). The layout changes over time, but never within a month. The rule is therefore strict: identical schema.
- SIA-BI has files with a `_1`, `_2` or `_3` suffix (in MG, RJ and SP, from 2023-05: 117 state-months), in 30 of them next to a file without a suffix. Only their names were seen, in the same listing; their content was not examined, and DbSus does not treat them as parts of one table.

### FTP server

- `ftp.datasus.gov.br`, anonymous, IIS. Supports `SIZE`, `MDTM` (UTC, seconds), `REST STREAM`, `EPSV`/`PASV`. No `MLSD`: only DOS-format `LIST` (two-digit years, minute precision). No TLS: the `FEAT` reply lists `AUTH TLS`, but `AUTH TLS` is refused with `534 Local policy on server does not allow TLS secure connections.` (tried once, October 2026).
- Public files live under `/dissemin/publicos/` (e.g. `SIHSUS/200801_/Dados/RD{UF}{YYMM}.dbc`, `SIASUS/200801_/Dados/PA{UF}{YYMM}{part}.dbc`).
- **Files are republished** under the same name with different content, so anything cached or pinned must be keyed on size and modification time (or a hash), not on the name.
- **No HTTP(S) mirror:** the FTP host does not answer on ports 80/443. The website's download page uses undocumented, form-driven endpoints that return HTML; the library does not use them.

## API and behavior

### Naming

- Parameter names follow one rule: an options parameter is `options` when the method takes one kind (`DbfReader`, `DbcFile`) and `dbfOptions` / `dbcOptions` when it takes both (`DbSusFile`, `DatasusClient`); a stream is named after its format (`dbf`, `dbc`); paths are `path` / `paths`, or `dbcPath` / `dbfPath` when a method takes two; the parts of a split table are `parts`. Renaming a parameter breaks callers that pass it by name, so the names are settled before the first release.

### DBC

- `DbcFile.OpenAsDbf(path | Stream)` returns a read-only, forward-only stream that decompresses on demand. The DBF header and checksum are read at open, so an invalid file fails immediately; the input is read strictly sequentially (network streams work).
- `DbcFile.ConvertToDbf(paths | streams)`: the path overload writes to a temporary file and moves it into place only after success, checksum included; the stream overload writes as it goes.
- Options are an object (`DbcReadOptions`, `DbfReadOptions`), not boolean parameters, so new options do not change signatures. `VerifyChecksum` is on by default.
- The one boolean parameter is `leaveOpen`, the last parameter of every method that takes a stream: who owns a stream is decided per call, not per options object. With `leaveOpen: false` (the default) the stream belongs to DbSus from the call on: it is disposed with the returned reader or stream, and also if the call throws, so `Open(File.OpenRead(path))` never leaks. With `leaveOpen: true` it is never disposed. Methods that take paths open and close their own files; `DbcFile.ConvertToDbf(Stream, Stream)` never disposes either stream.
- Checksum failures are `InvalidDataException` (a sealed type, so there is no subclass); `DbcReadOptions.ChecksumMismatch` is the hook for reacting to them specifically. The callback receives a `DbcChecksumMismatch` (part index and name, stored and computed CRC-32), a class so it can grow without breaking callers.
- The decoder is pull-based and can suspend mid-copy, so callers can read in blocks of any size.

### DBF reader

- One type: `DbfReader : DbDataReader`, usable with `DataTable.Load`, `SqlBulkCopy` and Dapper. Not thread-safe; the inherited async methods complete synchronously.
- **Types:** `C` → `string`; `N` without decimals → `int` (width ≤ 9), `long` (≤ 18), `decimal` (wider); `N` with decimals and `F` → `decimal`; `D` → `DateTime`; `L` → `bool`. Unsupported types (e.g. memo) are exposed as raw bytes and flagged, never as text.
- **Nulls:** blank `N`/`F`/`D`/`L`, `?` in `L` and all-zero dates are `DBNull`; blank `C` is `""` unless `BlankStringsAsNull`. Trailing spaces are trimmed, leading spaces kept.
- **Errors** are `FormatException` naming the field, the record and the part, never the value, because DATASUS data can be personal.
- **No allocation per record** for `Read` and the typed getters (including `GetFieldValue<T>`; measured on .NET 8, 9 and 10 and on .NET Framework 4.8; its nullable forms box on every call on .NET 8, 24 bytes or 32 for `decimal?`, and on .NET Framework, about 24 bytes, because of the runtime's JIT, not the package build). A test guards it. `GetChars` decodes straight into the caller's buffer when the encoding is single-byte (as are the code pages of the LDIDs found in DATASUS files), so text can be read without allocating too; with a double-byte code page (932, 936, 949, 950) or another multi-byte encoding it allocates like `GetString`.
- **Access:** names ignore case; unknown names and ordinals throw `IndexOutOfRangeException` (ADO.NET contract); `TryGetOrdinal` does not throw. `Columns` projects and re-indexes fields. Deleted records are skipped unless requested. `DbfField.FileIndex` is the position in the file, not the ordinal, which counts only the exposed fields.
- **Checksum through the facade:** after the last record the reader drains the decompressed stream, so the `Read` that returns `false` is the one that reports a checksum mismatch.

### Character field conversions

- Opt-in, per field: `DbfFieldConversion.Int32()`, `Int64()`, `Decimal()` and `Date("yyyyMMdd" | "ddMMyyyy")`, only on `C` fields, validated at open. A converted field reports its target type everywhere; blank is null; a value that does not parse or does not fit is a `FormatException` (field, record and part, never the value).
- `Date("yyyyMM")` converts a year and month (such as a processing month) to the first day of that month. A year and a month stored in two separate fields are not combined: a conversion applies to one field.

### Split tables and finding parts

- The same `DbfReader` reads several parts as one table. Every part's header is compared with the first when the reader opens, before any record, so a bulk load never starts on an inconsistent set. `RecordNumber` counts across parts; each part's checksum is verified when its last record is read, and its file is closed right away. Parts come as file paths or, from streams, as a list of `DbcPart` (a stream and an optional name, `part N` when absent), one list rather than parallel lists of streams and names.
- There is no public way to read a DBF split into parts from streams any more (only DBC parts, as `DbcPart`); DBF parts given by path are still read as one table with `DbSusFile.OpenReader(paths)`.
- `DbSusFile.FindParts` returns the parts of a split table in letter order. Names match ignoring case on every OS; a gap is a `FileNotFoundException` naming the missing part; an unlettered file next to lettered parts is an `IOException`. A missing **last** part cannot be detected locally; the client uses the FTP listing, and does not rely on it alone: it asks the server (`SIZE`/`MDTM`, over the control connection) for the part after the last one listed, and for the file itself when the listing shows nothing, so a stale listing cannot hide a part. A listing counts only when the server confirms its end with a completion reply; FluentFTP 55 otherwise returns what it read as a success, so an unconfirmed listing is a transient failure and is never cached.

### Integrity and database loads

- The checksum can only be confirmed at the end of the data, so database loads should use a transaction or a staging table and commit only after the reader finished without an exception. The reading guide (`docs/reading.md`) shows this.
- Plain FTP plus CRC-32 detects corruption, not tampering.

### Client

- **Paths** are strings relative to a configurable root (`/dissemin/publicos`). URLs, `..`, `.`, empty segments, `\`, control characters and names invalid on Windows or Linux are rejected. For 53 file types the catalog builds them from a dataset, a period and, where the files are by state, a state (see Catalog, below).
- **FTP:** FluentFTP behind an internal interface; explicit `EncryptionMode = None`, raw `SIZE`/`MDTM`, `LIST` forced.
- **Concurrency:** the client is thread-safe; a pool lends at most `MaxConnections` connections (default 2, maximum 4).
- **Retries:** transient failures only (socket errors, timeouts, FTP 4xx, truncated transfers), exponential backoff with jitter capped at 30 s; `550` to a request for a file is `FileNotFoundException`. A listing refused with a permanent reply (5xx, `550` included) is `DatasusRequestRefusedException`, never retried and never a missing file: FTP uses `550` both for a folder that does not exist and for one that may not be read (the folder of preliminary data is the one exception, described with the catalog). FluentFTP 55 takes a `550` to `LIST` for an empty folder and returns no entries, so the client reads the reply itself (reproduced against a loopback server, October 2026).
- **Unexpected exceptions from FluentFTP** are transient failures too. FluentFTP 55.0.0 throws a `NullReferenceException` from a listing when the server has closed the control connection without notice: it retries the listing on the stream it has just closed (reproduced against a loopback server, October 2026). Only the calls into FluentFTP are wrapped, so that an exception of this library's own code is never retried or hidden; cancellation, invalid arguments, and the exceptions FluentFTP throws by design keep their own handling. The connection is not reused, and after the last retry the original exception is the inner exception of `DatasusRetriesExhaustedException`. `OnRetry` reports each retry (a description of the operation for people to read, the remote path, the attempt, the wait, the failure); its exceptions are ignored.
- **Failures** have their own types, all deriving from `IOException`: `DatasusRetriesExhaustedException` and `DatasusRequestRefusedException`, whose abstract base `DatasusTransferException` carries `Attempts`, `FtpReplyCode` and `RemotePath` (always set, `/` for the root), and `InsufficientDiskSpaceException`. A permanent FTP reply is never retried.
- **Timeouts and cancellation, intended for every runtime; verified on .NET 10 and .NET Framework 4.8.** On .NET Framework, FluentFTP's asynchronous reads observe neither its read timeout nor the cancellation token (they rely on a token that `NetworkStream.ReadAsync` there ignores once a read has started), so a server that stops answering would block forever. There the client uses FluentFTP's synchronous client, whose reads enforce the timeout, on the thread pool; elsewhere the asynchronous one. Canceling closes the connection's sockets, which is meant to end any pending read on every runtime; each read of a download is limited to `ReadTimeout` by the client itself; a connection closed by force is never returned to the pool. A canceled download leaves no cache entry, only its `.partial` file, from which the next download resumes. Since this relies on FluentFTP's protected connection hooks, on .NET Framework the client checks that FluentFTP reported each connection (after connecting; before a download can be read; right after a listing, which FluentFTP reads in the same call) and otherwise refuses that FluentFTP version with a `NotSupportedException`, not retried. FluentFTP is declared as a minimum (55.0.0) without an upper bound; a weekly workflow tests the newest release.
- **Transport messages** name the step (connecting, listing, reading the size and date, opening for reading) with the host and port. FluentFTP reports a failed TCP connect the same way for the control and the data connection, so the message does not say which one failed: it says whether FluentFTP reopened the control connection during the step and, when the step opens a passive data connection, gives a blocked passive connection as a possible cause only.
- **Cache:** `<cache>/<remote path>/<file>/<size>-<MDTM>/`, one folder per server version, so a new version never overwrites a file a reader has open. Interrupted downloads resume with `REST` only when size and time are unchanged. With `VerifyOnDownload` (default) the file is fully decompressed and its CRC checked before entering the cache. Per-entry lock files (`FileShare.None`; `flock` on Linux/macOS) coordinate processes. Old partial files are removed only when older than 24 hours and unlocked; with a size limit, least recently used entries are evicted, never one in use.
- **Without a cache:** downloads go to a temporary folder and are deleted when the reader is done.

### Catalog

- **Scope:** 53 file types of SIASUS, SIHSUS, CNES, SIM, SINASC, CIHA, CIH, SISPRENATAL, SISCOLO, SISMAMA, PCE, RESP, ESUSNOTIFICA and PO: 42 with one file per state and 11 with one file for the whole country and none by state. A file type enters the catalog only when the server's listings (October 2026) showed one regular folder and name pattern for it and one real file of it, with records, was read. Other file types that the listings showed are in the paths guide, outside the catalog. The catalog follows the server, not the DATASUS website: the two disagree on when some file types start (`docs/datasus-paths.md` has the table).
- **Shape, never availability.** A dataset checks that a period is a month for a monthly file type and a year for a yearly one, and that the state is one. It never checks whether the file exists: DATASUS publishes late, leaves gaps and does not have every state in every period, and constants about that would go out of date.
- **Eras.** A dataset is a list of eras: a range of periods with one folder and one name pattern, because DATASUS changed folders, prefixes and the digits of the year over time. Nine datasets have two eras, the one in use now and the one before it (SIASUS-PA, SIHSUS RD, SP and RJ, SIM DO, DOEXT, DOFET and DOINF, and SINASC-DN); the others have one. An era starts where its folder starts, not at its first file (SIM-DOREXT starts in 1996 with its folder, and its first file is of 2013; a folder with no date in its name starts at the first file listed): `FirstPeriod` is the start of the oldest era, and a period before it is an `ArgumentOutOfRangeException`. The hundred years that a two-digit year can name are counted in each era: the same name is 2007-12 in the older SIHSUS folder and would be 2107-12 in the one in use now. The limit is of every name with two digits of the year, monthly or yearly (PCE, RESP, ESUSNOTIFICA-DCCR and the five SIM file types for the whole country only), and not of the names with four. The eras themselves are not public.
- **The whole country.** Four datasets have, beside the files of the states, one file for the whole country (SIM-DO, SINASC-DN, PCE and RESP), and eleven have a file for the whole country and none by state; they are datasets like the others, and `Coverage` says which files a dataset has. The file for the whole country has methods of its own (`GetNationalPath`, `FindNationalTableAsync`, `ListNationalTablesAsync`, `OpenNationalReaderAsync`) and no member of `DatasusUf`, whose values are the IBGE codes of the states: a call that forgets the state must never open the file for the whole country, which can be tens of megabytes. No method returns both kinds. A dataset without the files asked for is a `NotSupportedException` from `DatasusDataset` (the object does not have them) and an `ArgumentException` from the client (the dataset is an argument). The name of the file for the whole country belongs to the era, like the name of the file of a state, and has no part letter.
- **Final and preliminary data.** Which years SIM and SINASC still publish as preliminary changes, so nothing about it is constant in the code: a dataset gives the candidate paths in order, final first, and the client asks the server. `OpenReaderAsync(dataset, uf, period)` opens final data only, so that preliminary data is never read by accident; preliminary data is opened from a `DatasusTableInfo`, where `IsPreliminary` is in plain sight. The folder of preliminary data is optional, since a source may stop having one: a `550` to its listing, and no other reply, is taken as there being no such folder. Because the same `550` is the answer for a folder that may not be read, a table not found then fails with a `FileNotFoundException` that says that the folder could not be listed and carries the reply; `ListTablesAsync` has no way to say it and returns the tables of the folder of final data.
- **Listing or asking.** `ListTablesAsync` is one directory listing per folder and inherits its limits (a partial listing omits files without any error; listings are cached). `FindTableAsync` asks the server for the file itself, as `FindPartsAsync` does, and is the reliable way to know whether a table exists.
- **Letter case.** Paths are built in upper case with a lower-case extension, while the server lists some names otherwise. The server ignored case in October 2026: for a file listed as `DORR2011.DBC`, `SIZE` answered `213` with the same size under that name and under `dorr2011.dbc`. Tables are nevertheless opened under the name the server lists, because the cache is keyed by the remote path and one file must not be cached under two spellings.
- **Names.** `DatasusDataset` and `DatasusCatalog`, not `Dataset`: one capital letter away from `System.Data.DataSet`, in a library whose reader is a `DbDataReader`; and the prefix matches `DatasusClient` and `DatasusFileInfo`.
- **`DatasusUf`** members are named by the abbreviation that file names carry, and their values are the IBGE codes of the states. There is no zero member: `default(DatasusUf)` is rejected rather than named.
- **`DatasusPeriod`** is a month or a year. A year is never equal to a month, and a year sorts before its own months.
- **`OfficialName` is in Portuguese,** a deliberate exception to the rule that everything in the code is in English: it is the name under which DATASUS publishes the file type, a proper name, kept as DATASUS spells it (including `Confeção`).
- **`OfficialName` may be `null`.** For three file types (SIHSUS CH, CM and MT) no name was found: the file transfer page of the DATASUS website does not list them, and the documents and definition files of the server that were searched do not name them. The property is `null` for them. No name is made up: one written here would pass as the name DATASUS publishes.
- **Files read.** By October 2026 one real file of each of the 53 file types, with at least one record, was read with DbSus: decompressed, its CRC-32 checked, and every field of every record read. It was usually a small and old file, and some held a single record. From the folders of preliminary data, two files were read in the same way in October 2026, found and opened through the catalog on the server: SIM-DO and SINASC-DN of Roraima, both of 2026. Of each of the nine older eras, one file with records was read in the same way in October 2026. Of the four file types that have files by state and a file for the whole country, the file for the whole country of PCE and of RESP was read in the same way, one each; those of SIM-DO and SINASC-DN had only their headers read, six in all. That says nothing about other periods or other files. The paths guide says, in a column of its tables, what was checked of each file type and of each older era.

## Performance

BenchmarkDotNet, Release, net10.0, AMD Ryzen 9 9950X3D, one large SIA-PA file (126 MB DBC, 1.17 GB DBF, 2,956,469 records × 60 fields):

| Benchmark | DBF throughput | Allocated per record |
|---|---|---|
| Decompress only | 311 MB/s | 0.05 B |
| Decompress + read records | 309 MB/s | 0.06 B |
| + 3 typed getters | 299 MB/s | 0.06 B |
| + 2 converted `C` fields | 293 MB/s | 0.06 B |
| Every value of every field (`GetValues`, boxed) | 189 MB/s | 1,867 B |

Decompression dominates typical use: it consumed the compressed file at about 33 MB/s (126 MB in about 3.8 s; see `docs/performance.md`), so a connection slower than that is the limit when files come from the DATASUS server.

## Testing

- **Oracle:** `tools/oracle` (Docker, `pyreaddbc` 2.0.4) records SHA-256 and size of each `.dbc` and its `.dbf` in `tests/data/expected.json`, so tests and CI do not need the oracle.
- **Decoder:** the upstream `blast` test vector, byte-for-byte agreement with the oracle on real files (including outputs of about 2 GB), decoding with random chunk sizes, and per-path coverage measurements. No available input uses Huffman-coded literals or the 2 KiB dictionary.
- **No microdata in the repository.** Samples are fetched by `tools/fetch-samples` into a git-ignored folder and checked against `expected.json`; tests that need them skip themselves when absent. The only committed data files are two small CNES tables with establishment-level data only (`tests/data/fixtures`).
- **Synthetic DBCs** built in memory (literal-only implode, independent CRC-32) cover the wrapper in CI.
- **Cross-process locks** are tested with two real processes on Windows (.NET 10 and .NET Framework) and Linux.
- **Client:** an in-memory fake server injects failures (dropped transfers, 421, republished and corrupt files, low disk space, cancellation, concurrency). Tests against the real server run only on request.
- **Package consumers in CI:** an outside application restores the packed packages and checks which build it loaded (net8.0 → `netstandard2.0`, net10.0 → `net10.0`, net481 → `net472`), runs again with the oldest accepted dependency versions pinned (NU1605 guard), and once more built with SDK 8, which, unlike SDK 10, does not prune framework packages from the restore.

## Packaging

- Each package ships `LICENSE` and `README.md`; `DbSus` also ships `THIRD-PARTY-NOTICES.md`. Symbols go to `.snupkg` with SourceLink; package validation is on.
- Packages are published only from CI builds (`ContinuousIntegrationBuild`), whose PDBs and DLLs carry `/_/` source paths instead of machine paths.
- Tags by purpose: `datareader` and `ado.net` only on `DbSus` (its ADO.NET integration); `ftp` and `download` only on `DbSus.Client`; the rest is shared.
- **Public API tracking** (Microsoft.CodeAnalysis.PublicApiAnalyzers): each library lists its public API in `PublicAPI.Shipped.txt` (what a published version contains) and `PublicAPI.Unshipped.txt` (what changed since). The build fails when the code and the files disagree, so every API change shows up in a review as a change to these files. The API is the same on every target framework, so there is one pair of files per project. Until the first release, `Shipped` is empty and the whole API is in `Unshipped`. When a version is published, `Unshipped` moves into `Shipped`; from then on, a removed or changed member appears in `Unshipped` as a `*REMOVED*` line. RS0026 is suppressed until that first move (see the project files).

## Open

- Real async in the core and a streaming (no download) mode in the client.
- The encoding fallback and LDID 0x02/0x57 on files with accented text.
