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

Twenty files across SIM, SINASC, SINAN, SIH, SIA and CNES were examined in aggregate (no individual values recorded):

- **Header:** field descriptors end at 0x0D, at an empty name, or where the header length runs out (the CNES file above has no terminator at all). Spare bytes after the last field are tolerated; in every file examined the fields fill the record exactly.
- **Field types:** `C` everywhere, `N` in most sources, `D` only in SINAN; no `F`, `L` or `M`.
- **Numbers:** left-padded with spaces, decimal point `.`, digits after the point as declared. No negatives, no `*` overflow, no blank `N` values seen.
- **Dates in character fields** use `YYYYMMDD` in SIH, SIA and CNES but `DDMMYYYY` in SIM and SINASC; they cannot be inferred from field names.
- **Opaque bytes:** some identifier fields (in CNES professional and SIA APAC/BI/PS files) hold bytes above 0x7F that are not text in any code page, so raw access to field bytes is needed.
- **Deletion flag:** blank on every record examined.
- **File names:** some files use an upper-case extension (`.DBC`).

### Language driver (LDID) and code pages

- Values seen: 0x00 (no driver) in SIM, SINASC, SINAN, SIH SP/ER, SIA AM/AQ/PS and CNES SR/ST; 0x02 in SIH RD/RJ; 0x03 in SIA PA/BI; 0x57 in SIA AD/AR; 0x58 in CNES EQ/LT/PF/HB.
- Meaning: 0x02 = code page 850 and 0x03 = 1252 (Microsoft's Visual FoxPro table); 0x58 = 1252, 0x57 = ISO-8859-1 and 0x59 = 1252 (dBASE drivers, per the MiraMon documentation and GDAL's LDID table).
- The only accented text found (CNES) reads correctly as code page 1252, including in a file whose LDID is 0x00.
- **To validate:** the 1252 fallback for LDID 0x00, and whether files marked 0x02 and 0x57 really hold text in those code pages.

### Split tables (SIA-PA)

- Among the file types surveyed (the SIASUS, SIHSUS, CNES, SIM, SINASC and SINAN data folders listed on the server in October 2026), only SIA-PA is split into lettered parts, `a` to `d`. The headers of all 740 parts of the 307 split groups in `SIASUS/200801_/Dados` (fetched with range requests) were compared: no gaps, no group starting after `a`, never an unlettered file next to lettered parts, and all 434 part-to-part comparisons identical (names, types, widths, decimals, record and header length, LDID, version). The layout changes over time, but never within a month. The rule is therefore strict: identical schema.
- SIA-BI has files with a `_1`, `_2` or `_3` suffix (in MG, RJ and SP, from 2023-05: 117 state-months), in 30 of them next to a file without a suffix. Only their names were seen, in the same listing; their content was not examined, and DbSus does not treat them as parts of one table.

### FTP server

- `ftp.datasus.gov.br`, anonymous, IIS. Supports `SIZE`, `MDTM` (UTC, seconds), `REST STREAM`, `EPSV`/`PASV`. No `MLSD`: only DOS-format `LIST` (two-digit years, minute precision). No TLS: the `FEAT` reply lists `AUTH TLS`, but `AUTH TLS` is refused with `534 Local policy on server does not allow TLS secure connections.` (tried once, October 2026).
- Public files live under `/dissemin/publicos/` (e.g. `SIHSUS/200801_/Dados/RD{UF}{YYMM}.dbc`, `SIASUS/200801_/Dados/PA{UF}{YYMM}{part}.dbc`).
- **Files are republished** under the same name with different content, so anything cached or pinned must be keyed on size and modification time (or a hash), not on the name.
- **No HTTP(S) mirror:** the FTP host does not answer on ports 80/443. The website's download page uses undocumented, form-driven endpoints that return HTML; the library does not use them.

## API and behaviour

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

- **Paths** are strings relative to a configurable root (`/dissemin/publicos`). URLs, `..`, `.`, empty segments, `\`, control characters and names invalid on Windows or Linux are rejected. A structured catalogue (source, modality, year, month, state) is a later phase.
- **FTP:** FluentFTP behind an internal interface; explicit `EncryptionMode = None`, raw `SIZE`/`MDTM`, `LIST` forced.
- **Concurrency:** the client is thread-safe; a pool lends at most `MaxConnections` connections (default 2, maximum 4).
- **Retries:** transient failures only (socket errors, timeouts, FTP 4xx, truncated transfers), exponential backoff with jitter capped at 30 s; `550` is `FileNotFoundException`. `OnRetry` reports each retry (a description of the operation for people to read, the remote path, the attempt, the wait, the failure); its exceptions are ignored.
- **Failures** have their own types, all deriving from `IOException`: `DatasusRetriesExhaustedException` and `DatasusRequestRefusedException`, whose abstract base `DatasusTransferException` carries `Attempts`, `FtpReplyCode` and `RemotePath` (always set, `/` for the root), and `InsufficientDiskSpaceException`. A permanent FTP reply is never retried.
- **Timeouts and cancellation, intended for every runtime; verified on .NET 10 and .NET Framework 4.8.** On .NET Framework, FluentFTP's asynchronous reads observe neither its read timeout nor the cancellation token (they rely on a token that `NetworkStream.ReadAsync` there ignores once a read has started), so a server that stops answering would block forever. There the client uses FluentFTP's synchronous client, whose reads enforce the timeout, on the thread pool; elsewhere the asynchronous one. Cancelling closes the connection's sockets, which is meant to end any pending read on every runtime; each read of a download is limited to `ReadTimeout` by the client itself; a connection closed by force is never returned to the pool. A cancelled download leaves no cache entry, only its `.partial` file, from which the next download resumes. Since this relies on FluentFTP's protected connection hooks, on .NET Framework the client checks that FluentFTP reported each connection (after connecting; before a download can be read; right after a listing, which FluentFTP reads in the same call) and otherwise refuses that FluentFTP version with a `NotSupportedException`, not retried. FluentFTP is declared as a minimum (55.0.0) without an upper bound; a weekly workflow tests the newest release.
- **Transport messages** name the step (connecting, listing, reading the size and date, opening for reading) with the host and port. FluentFTP reports a failed TCP connect the same way for the control and the data connection, so the message does not say which one failed: it says whether FluentFTP reopened the control connection during the step and, when the step opens a passive data connection, gives a blocked passive connection as a possible cause only.
- **Cache:** `<cache>/<remote path>/<file>/<size>-<MDTM>/`, one folder per server version, so a new version never overwrites a file a reader has open. Interrupted downloads resume with `REST` only when size and time are unchanged. With `VerifyOnDownload` (default) the file is fully decompressed and its CRC checked before entering the cache. Per-entry lock files (`FileShare.None`; `flock` on Linux/macOS) coordinate processes. Old partial files are removed only when older than 24 hours and unlocked; with a size limit, least recently used entries are evicted, never one in use.
- **Without a cache:** downloads go to a temporary folder and are deleted when the reader is done.

## Performance

BenchmarkDotNet, Release, net10.0, AMD Ryzen 9 9950X3D, one large SIA-PA file (126 MB DBC, 1.17 GB DBF, 2,956,469 records × 60 fields):

| Benchmark | DBF throughput | Allocated per record |
|---|---|---|
| Decompress only | 311 MB/s | 0.05 B |
| Decompress + read records | 309 MB/s | 0.06 B |
| + 3 typed getters | 299 MB/s | 0.06 B |
| + 2 converted `C` fields | 293 MB/s | 0.06 B |
| Every value of every field (`GetValues`, boxed) | 189 MB/s | 1,867 B |

Decompression dominates typical use; the network will usually be slower still. No optimisation has been done yet.

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
