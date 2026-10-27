# FTP troubleshooting

What `DbSus.Client` does when a connection to the DATASUS FTP server fails or stalls, how long it can take, and what the messages say.

## Two connections

FTP uses two connections: the control connection, to port 21 by default, and a data connection for each listing and each download, to a port the server chooses (passive mode). A failure message names the step (`connecting to`, `listing`, `reading the size and date of`, `opening ... for reading`) with the host and port.

The underlying FTP library reports a failed connection attempt the same way for both connections (for example `Timed out trying to connect to IP #1`, the number being the position of the address tried). So the message says what is known: whether the control connection was reopened during the step and, for listings and downloads, that a blocked passive data connection is a possible cause.

Receiving the server's greeting (`220`) on port 21 shows that the control connection can be opened; it does not show that the passive data ports can be reached.

## What the client does

- **Passive mode only.** For each listing and each download it sends `PASV` and connects to `Host`, on the port of the reply; the address in the reply is ignored, so a server behind NAT that announces a private address still works, and a server that sends its data from another address does not. Over IPv6 it sends `EPSV` instead, which also connects to `Host`. A data connection that times out or is refused is not tried in another mode.
- **IPv4 and IPv6.** The host name's addresses are tried in the order the system's DNS returns them, for the control connection and again for each data connection (see Data connections in [Compatibility](https://github.com/euclydesuchoas/dbsus/blob/main/docs/compatibility.md)).
- **Time.** Each connection attempt waits up to `ConnectTimeout` (30 seconds by default) per address, and a failed step is retried `MaxRetries` times (4 by default), the connection that failed being discarded, so a server that never answers can take minutes before the exception. Opening a transfer that resumes a partial download has a limit of its own, 5 seconds, from `PASV` to the reply to `RETR`. When that opening fails, the client tries again on a new connection; once a resumption has failed to open, a later attempt of the same download starts over from the beginning of the file instead, if at most 16 MiB had been received or if it is the last attempt.
- **Settings.** The data connection mode and the address family cannot be changed through `DatasusClientOptions`; `Host`, `Port`, `ConnectTimeout` and `ReadTimeout` can.

To see each failed attempt as it happens, set `OnRetry` and log its `Operation`, `RemotePath`, `FtpReplyCode` and `Exception.Message`.

## Worst case

Calculated from the default options, for a listing or a download whose data connection fails: 5 attempts, with waits of 7.5 to 15 seconds in all between them; the data connection goes to a single address, once per attempt, for a `Host` that has a single address, as the DATASUS server's had in October 2026; with several addresses, it tries them in order.

| The data connection... | Per attempt | Until the exception |
|---|---|---|
| never answers | `ConnectTimeout`: 30 s | 2 min 37.5 s to 2 min 45 s |
| opens, but no listing data arrives | `ReadTimeout` reading (60 s), then `ReadTimeout` waiting for the server's final reply (60 s): 120 s | 10 min 7.5 s to 10 min 15 s |
| opens, but no download data arrives | `ReadTimeout` (60 s); the client then closes the connection, so there is no wait for a final reply | 5 min 7.5 s to 5 min 15 s |

The same formulas were checked against a local test server with small timeouts (`ConnectTimeout` 200 ms, `ReadTimeout` 300 ms, `InitialRetryDelay` 100 ms), on .NET 10 and .NET Framework 4.8.1. `ReadTimeout` limits each wait for data, not a whole transfer: a download that keeps receiving data slowly is never cut by it.

These waits end at their timeouts, and canceling the token ends them at once (the connection is closed and not reused). This was verified on .NET 10 and .NET Framework 4.8.1; .NET 8 and 9, Mono, Unity and Xamarin were not checked.

In a web request or any call with a deadline, lower `MaxRetries`, `ConnectTimeout` and `ReadTimeout`, and pass a cancellation token that carries the deadline.

## What to check

- Logging in and changing directory (`CWD`) use only the control connection; a listing or a download also needs a data connection. If, with any FTP client, the first two work but a listing times out, look at the data connection first.
- Try another FTP client, in passive mode, and another network.
- A firewall or NAT has to let through the passive data connections, to the ports the server announces, not only port 21.
- Try without a VPN, and check whether an antivirus or security suite filters FTP traffic.
- Set `OnRetry` and log each failed attempt (see above).

## A known limit of listings

A listing counts only when the server confirms its end (a `226` reply after the data); one without that confirmation is a transient failure and is not cached.

- **A refused listing.** A listing that the server refuses with a permanent reply (5xx) is not a transient failure: it is a `DatasusRequestRefusedException`, with the code in `FtpReplyCode` and the reply in the message, and it is not retried. FTP uses `550` both for a folder that does not exist and for one that may not be read; in October 2026 the DATASUS server answered `550 Access is denied.` to the listing of one of its folders.
- **The folder of preliminary data.** SIM, SINASC, ESUSNOTIFICA and SINAN have one, and it is the one case in which the client takes a `550` to a listing as "there is no such folder", because that folder is optional. `ListTablesAsync` and `ListNationalTablesAsync` then return the tables of the folder of final data only. When `FindTableAsync` or `FindNationalTableAsync` finds no table, its `FileNotFoundException` says that the folder of preliminary data could not be listed and has the reply, so a folder that may not be read is not hidden. Any other permanent reply to that listing is still a `DatasusRequestRefusedException`.
- **What is not detected.** If the client's read of the listing fails while the server still answers `226`, the client cannot tell, and a partial listing can pass.

For split tables, `FindPartsAsync` reduces the risk: it asks the server directly (`SIZE` and `MDTM`, over the control connection) for the part after the last one listed, continuing until a part is missing, and for the file and its parts when the listing shows none of them. It does not cover what `ListAsync` returns, which is the listing as received (and reused for `ListingCacheDuration`), nor a part missing from the middle of a partial listing, which is reported as a missing part (`FileNotFoundException`) rather than looked up.
