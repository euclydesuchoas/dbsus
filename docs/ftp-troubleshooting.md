# FTP troubleshooting

What `DbSus.Client` does when a connection to the DATASUS FTP server fails or stalls, how long it can take, and what the messages say.

## Two connections

FTP uses two connections: the control connection, to port 21 by default, and a data connection for each listing and each download, to a port the server chooses (passive mode). A failure message names the step (`connecting to`, `listing`, `reading the size and date of`, `opening ... for reading`) with the host and port.

The underlying FTP library reports a failed connection attempt the same way for both connections (for example `Timed out trying to connect to IP #1`, the number being the position of the address tried). So the message says what is known: whether the control connection was reopened during the step and, for listings and downloads, that a blocked passive data connection is a possible cause.

Receiving the server's greeting (`220`) on port 21 shows that the control connection can be opened; it does not show that the passive data ports can be reached.

## What the client does

- **Passive mode only.** It uses `EPSV` when the server lists it in its `FEAT` reply, and `PASV` otherwise. It switches from `EPSV` to `PASV` only when the server answers `EPSV` with an error reply (4xx or 5xx) and the control connection uses IPv4; a data connection that times out or is refused after `EPSV` is not retried with `PASV`. With `EPSV` the data connection goes to the same address as the control connection; with `PASV`, to the address the server announces (or to the control connection's host, when the announced address is a private one). The DATASUS server does not list `EPSV` in its `FEAT` reply (seen in October 2026), so in practice the client uses `PASV` with it.
- **IPv4 and IPv6.** The host name's addresses are tried in the order the system's DNS returns them.
- **Time.** Each connection attempt waits up to `ConnectTimeout` (30 seconds by default) per address, and a failed step is retried `MaxRetries` times (4 by default), the connection that failed being discarded, so a server that never answers can take minutes before the exception.
- **Settings.** The data connection mode and the address family cannot be changed through `DatasusClientOptions`; `Host`, `Port`, `ConnectTimeout` and `ReadTimeout` can.

To see each failed attempt as it happens, set `OnRetry` and log its `Operation`, `RemotePath` and `Exception.Message`.

## Worst case

Calculated from the default options, for a listing or a download whose data connection fails: 5 attempts, with waits of 7.5 to 15 seconds in all between them; the data connection goes to a single address, once per attempt.

| The data connection... | Per attempt | Until the exception |
|---|---|---|
| never answers | `ConnectTimeout`: 30 s | 2 min 37.5 s to 2 min 45 s |
| opens, but no listing data arrives | `ReadTimeout` reading (60 s), then `ReadTimeout` waiting for the server's final reply (60 s): 120 s | 10 min 7.5 s to 10 min 15 s |
| opens, but no download data arrives | `ReadTimeout` (60 s); the client then closes the connection, so there is no wait for a final reply | 5 min 7.5 s to 5 min 15 s |

The same formulas were checked against a local test server with small timeouts (`ConnectTimeout` 200 ms, `ReadTimeout` 300 ms, `InitialRetryDelay` 100 ms), on .NET 10 and .NET Framework 4.8. `ReadTimeout` limits each wait for data, not a whole transfer: a download that keeps receiving data slowly is never cut by it.

These waits end at their timeouts, and canceling the token ends them at once (the connection is closed and not reused). This was verified on .NET 10 and .NET Framework 4.8; .NET 8 and 9, Mono, Unity and Xamarin were not checked.

In a web request or any call with a deadline, lower `MaxRetries`, `ConnectTimeout` and `ReadTimeout`, and pass a cancellation token that carries the deadline.

## A known limit of listings

A listing counts only when the server confirms its end (a `226` reply after the data); one without that confirmation is a transient failure and is not cached. But if the client's read of the listing fails while the server still answers `226`, the client cannot tell, and a partial listing can pass.

For split tables, `FindPartsAsync` reduces the risk: it asks the server directly (`SIZE` and `MDTM`, over the control connection) for the part after the last one listed, continuing until a part is missing, and for the file and its parts when the listing shows none of them. It does not cover what `ListAsync` returns, which is the listing as received (and reused for `ListingCacheDuration`), nor a part missing from the middle of a partial listing, which is reported as a missing part (`FileNotFoundException`) rather than looked up.
