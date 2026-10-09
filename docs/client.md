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

## Behavior

- **Paths** are relative to `/dissemin/publicos` (`RootPath`). URLs, `..`, backslashes and names that are invalid on Windows or Linux are rejected.
- **Connections:** at most two by default (`MaxConnections`, four at most), to be gentle with the server; the limit applies per instance.
- **Retries:** transient failures (dropped connections, timeouts, FTP 4xx replies, truncated transfers) are retried up to `MaxRetries` times (4 by default). The nominal wait starts at `InitialRetryDelay` (1 second by default) and doubles each time, up to 30 seconds; each actual wait is a random fraction, between half and all, of the nominal one. A missing file (FTP 550) is never retried. To log retries, set `OnRetry`: before each wait, it receives a description of the operation (for people to read), the remote path, the attempt that failed, the wait, the failure and its FTP reply code; its exceptions are ignored.
- **Downloads** resume after a dropped connection, restart from zero if the file changed on the server, and are verified (CRC-32) before being used or cached (`VerifyOnDownload`). Even with a cache, every download first asks the server for the file's size and time, to detect republished files.
- **The cache is optional.** Entries are keyed by path, size and modification time, written atomically, shared safely between threads and processes, and never removed while a reader has them open. `PruneCacheAsync` and `MaxCacheSizeBytes` keep it tidy. Each entry has a small `entry.lock` file used to coordinate threads and processes; pruning keeps lock files and the folders that become empty (a lock file must not vanish while another process may be waiting on it), and `ClearCacheAsync` removes entries not in use together with their lock files and empty folders, for when no other process is downloading into the same cache.
- **Without a cache** (`CacheDirectory` not set), each download goes to its own folder under the system temporary directory, `Path.GetTempPath()/DbSus/downloads/<random id>/` (on Windows usually `%TEMP%\DbSus\downloads`). That location cannot be changed: **to keep downloaded files in a folder you control, set `CacheDirectory`.** Files opened by `OpenReaderAsync` are deleted when the reader is disposed; a file returned by `DownloadAsync` is yours to delete. Folders left behind for more than 24 hours, whose files are not open, are removed later.
- **A canceled download** leaves no cache entry, only its `.partial` file, from which the next download resumes.

When a connection fails or takes long, see [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md).

## Exceptions

| Exception | When |
|---|---|
| `ArgumentException` | The path is not acceptable (the message says why). |
| `FileNotFoundException` | The file does not exist (FTP 550), or a part before the last one is missing. |
| `DatasusTransferException` | The base of the next two: a transfer with the server failed. Catch it to handle both; `Attempts`, `FtpReplyCode` and `RemotePath` are defined here. |
| `DatasusRetriesExhaustedException` | Transient failures persisted after every retry. `Attempts`, `FtpReplyCode` (e.g. `421`) and `RemotePath` tell the details. |
| `DatasusRequestRefusedException` | The server refused the request with a permanent reply other than 550 (e.g. `530`); not retried. Same properties. |
| `InsufficientDiskSpaceException` | Not enough free space for the file (`Directory`, `RequiredBytes`, `AvailableBytes`), checked before the transfer. |
| `IOException` | Part names on the server are ambiguous, an invalid cached copy is still open by a reader, or a file of a cache entry could not be written (for example, on .NET Framework without long path support, a path too long for a deep `CacheDirectory`: see [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)); in that case the original exception is the inner exception. The three exceptions above also derive from `IOException`, so a `catch (IOException)` still sees them. |
| `InvalidDataException` | A downloaded `.dbc` failed its CRC-32 check twice, a remote header is invalid, or, while reading, the data is corrupt or fails its checksum at the end. |
| `TimeoutException` | Another thread or process held the same cache entry for longer than `LockTimeout` (10 minutes by default). |
| `OperationCanceledException` | The cancellation token was canceled. |
| `NotSupportedException` | On .NET Framework only: the FluentFTP version in use did not report its connections to the client (see [compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)). |
