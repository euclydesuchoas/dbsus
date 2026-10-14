# Downloading from DATASUS with DbSus.Client

`DbSus.Client` reads from the DATASUS FTP server: directory listings, file size and date, the parts of a split table, headers without a full download, and resumable, verified downloads with an optional cache.

## A tour

```csharp
using System;
using System.Threading;
using DbSus.Client;

// Thread-safe: one instance can be shared, e.g. services.AddSingleton(client).
await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = @"D:\datasus-cache" });
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));   // every method takes a token
var token = timeout.Token;

// Directory listing (names, sizes; reused for ListingCacheDuration, 10 minutes by default).
var entries = await client.ListAsync("/SIHSUS/200801_/Dados", token);

// Exact size and modification time (UTC) of one file.
var info = await client.GetInfoAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", token);

// The parts of a split table, from the server's listing, checked by asking for the part after the last one.
var parts = await client.FindPartsAsync("/SIASUS/200801_/Dados/PAMG2407.dbc", token);

// Schema and record count from the first few kilobytes, without downloading the file.
var header = await client.ReadHeaderAsync("/CNES/200508_/Dados/LT/LTRR0610.dbc", token);

// Download to a local path (a cache entry here), reused while the server's size and time are unchanged.
string local = await client.DownloadAsync("/SIHSUS/200801_/Dados/RDRR2206.dbc", token);

// Every part of SIA-PA MG 2024-07, downloaded once, then read as one table.
using var reader = await client.OpenReaderAsync("/SIASUS/200801_/Dados/PAMG2407.dbc", cancellationToken: token);
```

## By dataset, state and period

For 112 file types, `DatasusCatalog` builds the path, and the client finds, lists and opens the table, from a dataset, a state and a period:

```csharp
using System;
using DbSus.Client;

await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });

// The path, built without reaching the server: /SIHSUS/200801_/Dados/RDRR2206.dbc
string path = DatasusCatalog.SihSus.RD.GetPath(DatasusUf.RR, DatasusPeriod.OfMonth(2022, 6));

// The table on the server: its files with exact sizes and times, and whether it is preliminary.
DatasusTableInfo table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025));

// What the folder lists, one entry per table.
var year = await client.ListTablesAsync(DatasusCatalog.Cnes.LT, DatasusPeriod.OfMonth(2024, 1), DatasusPeriod.OfMonth(2024, 12), DatasusUf.RR);

// Final data only; preliminary data is opened from a table found first: client.OpenReaderAsync(table).
using var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7));
```

- The catalog checks the shape of a request (a month for a monthly dataset, a real state), never whether the file exists.
- `OpenReaderAsync(dataset, uf, period)` opens final data only; `FindTableAsync` looks in the folder of final data and then in the folder of preliminary data, and says which.
- `ListTablesAsync` shows what the directory listing shows, which can be incomplete or out of date; `FindTableAsync` asks the server for the file itself.
- SIM-DO, SINASC-DN, PCE and RESP also have one file per year for the whole country, with methods of its own: `GetNationalPath`, `FindNationalTableAsync`, `ListNationalTablesAsync` and `OpenNationalReaderAsync` (final data only). Such a table has no state (`DatasusTableInfo.Uf` is `null`), and the methods by state never return it. Another 69 (every one of SINAN, SIM-DOFET and PO among them) have a file for the whole country and none by state: they are reached with the same methods, and the methods by state fail for them with `ArgumentException`. In October 2026, thirteen SINAN file types had files in the folder of preliminary data only: for them `OpenNationalReaderAsync` fails, and the table is found with `FindNationalTableAsync` and opened by choice. One SINAN file type, INFL, had no file in either folder: the catalog has it because the DATASUS website lists it, the methods find no table, and the `FileNotFoundException` says that no file of it was known.
- A table of SIASUS-BI of a large state is in numbered parts (`BIMG2312_1.dbc`, `BIMG2312_2.dbc`), sometimes beside a file without a number. The methods that take a dataset read the numbered parts as one table and leave that file aside; `FindPartsAsync` and `OpenReaderAsync` with a path know lettered parts only, and for them each of those names is a file of its own.

The file types, their folders and names, and what the catalog leaves out are in [DATASUS paths and file names](https://github.com/euclydesuchoas/dbsus/blob/main/docs/datasus-paths.md).

## Behavior

- **Paths** are relative to `/dissemin/publicos` (`RootPath`). URLs, `..`, backslashes and names that are invalid on Windows or Linux are rejected.
- **Connections:** at most two by default (`MaxConnections`, four at most), to be gentle with the server; the limit applies per instance.
- **Retries:** transient failures (dropped connections, timeouts, FTP 4xx replies, truncated transfers) are retried up to `MaxRetries` times (4 by default), each time on another connection.
  - **Never retried:** a missing file (FTP 550), and a request that the server refuses with a permanent reply (FTP 5xx), a listing refused with 550 included.
  - **Waits:** the nominal wait starts at `InitialRetryDelay` (1 second by default) and doubles each time, up to 30 seconds; each actual wait is a random fraction, between half and all, of the nominal one.
  - **Unexpected exceptions from the FTP library:** an exception that the FTP library (FluentFTP) is not meant to throw counts as a transient failure. FluentFTP 55.0.0 throws a `NullReferenceException` from a listing whose control connection the server has closed without notice. If it persists, it is the inner exception of the `DatasusRetriesExhaustedException`.
  - **Logging:** set `OnRetry`. Before each wait, it receives a description of the operation (for people to read), the remote path, the attempt that failed, the wait, the failure and its FTP reply code; its exceptions are ignored.
- **Downloads** resume after a dropped connection, restart from zero if the file changed on the server, and are verified (CRC-32) before being used or cached (`VerifyOnDownload`). Even with a cache, every download first asks the server for the file's size and time, to detect republished files.
- **Nothing is read before the download ends.** `OpenReaderAsync` and `OpenNationalReaderAsync` download the file, or every part of the table, one after the other, to the cache or to the temporary folder, and only then open the reader: there is no mode that reads the data as it arrives. `VerifyOnDownload = false` spares the decompression that checks a `.dbc` when its download ends, and then a CRC-32 that does not match shows only at the end of the reading (with a cache, the entry is marked invalid).
- **The cache is optional.** Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy. Each entry has a small `entry.lock` file used to coordinate threads and processes; pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it), and `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, for when no other process is downloading into the same cache. Use a folder for the cache only: `ClearCacheAsync` removes every empty folder below it. Junctions and symbolic links inside it are never entered.
- **Without a cache** (`CacheDirectory` not set), each download goes to its own folder under the system temporary directory, `Path.GetTempPath()/DbSus/downloads/<random id>/` (on Windows usually `%TEMP%\DbSus\downloads`). That location cannot be changed: **to keep downloaded files in a folder you control, set `CacheDirectory`.** Files opened by `OpenReaderAsync` are deleted when the reader is disposed; a file returned by `DownloadAsync` is yours to delete. Folders left behind for more than 24 hours, whose files are not open, are removed later.
- **A canceled download** leaves no cache entry, only its `.partial` file, from which the next download resumes.

When a connection fails or takes long, see [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md).

## Exceptions

| Exception | When |
|---|---|
| `ArgumentException` | The path is not acceptable (the message says why), or a request to the catalog is malformed: a year where a month is needed, a period that is not set, a dataset the catalog does not have, or a dataset without the files asked for (by state, or for the whole country). |
| `ArgumentOutOfRangeException` | In the catalog: a value that is not a state; a period before the oldest folder layout the catalog knows for the file type; or, for a dataset whose file names carry two digits of the year (every monthly one, and the yearly ones of PCE, RESP, ESUSNOTIFICA-DCCR, the five SIM file types for the whole country only and every one of SINAN), a period more than ninety-nine years after the start of the layout in use now, whose file name would be the one of an earlier period. |
| `FileNotFoundException` | The file does not exist (FTP 550 to a request for a file), a part before the last one is missing, or the server has no table for a dataset and period, of a state or for the whole country (the message says when only a preliminary one exists, and when the folder of preliminary data could not be listed). |
| `DatasusTransferException` | The base of the next two: a transfer with the server failed. Catch it to handle both; `Attempts`, `FtpReplyCode` and `RemotePath` are defined here. |
| `DatasusRetriesExhaustedException` | Transient failures persisted after every retry. `Attempts`, `FtpReplyCode` (e.g. `421`) and `RemotePath` tell the details. |
| `DatasusRequestRefusedException` | The server refused the request with a permanent reply (FTP 5xx, e.g. `530`); not retried. Same properties. For a file, 550 is not one of these: it is the missing file above. For a listing, 550 is one of these: FTP uses it both for a folder that does not exist and for one that may not be read, so a refused listing is never reported as a missing file, and `FindTableAsync`, `ListTablesAsync`, `FindPartsAsync` and `OpenReaderAsync`, which list a folder first, let it through. The one exception is the folder of preliminary data (SIM, SINASC, ESUSNOTIFICA and SINAN have one), which is optional: a 550 to its listing is taken as there being no such folder. |
| `InsufficientDiskSpaceException` | Not enough free space for the file (`Directory`, `RequiredBytes`, `AvailableBytes`), checked before the transfer. |
| `IOException` | A file of a cache entry could not be written (for example, on .NET Framework without long path support, a path too long for a deep `CacheDirectory`: see [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)). The original exception is the inner exception. |
| `IOException` | Part names on the server are ambiguous, or an invalid cached copy is still open by a reader. `DatasusRetriesExhaustedException`, `DatasusRequestRefusedException` and `InsufficientDiskSpaceException` also derive from `IOException`, so a `catch (IOException)` still sees them. |
| `InvalidDataException` | A downloaded `.dbc` failed its CRC-32 check twice, a remote header is invalid, or, while reading, the data is corrupt or fails its checksum at the end. |
| `TimeoutException` | Another thread or process held the same cache entry for longer than `LockTimeout` (10 minutes by default). |
| `OperationCanceledException` | The cancellation token was canceled. |
| `NotSupportedException` | From `DatasusDataset`: the path asked for is of files the file type does not have (`GetNationalPath` of one with files by state only, or `GetPath` of one with a file for the whole country only). From the client, on .NET Framework only: the FluentFTP version in use did not report its connections to the client (see [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)). |
