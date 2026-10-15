# Security policy

## Reporting a vulnerability

Report it in private, through GitHub's private vulnerability reporting: open the **Security** page of the repository and choose **Report a vulnerability**. Do not open a public issue for it. There is no e-mail address for reports.

Say which package and version, the target framework and the operating system, and what it takes to reproduce the problem. If a file sets it off and the file is one that DATASUS distributes, give its path on the server instead of attaching it; attach it only if you made it.

## Supported versions

Only the latest published version of `DbSus` and of `DbSus.Client` gets fixes. There are no backports during 0.x.

## What is a vulnerability of this library

DbSus reads files that come from somewhere else, and `DbSus.Client` gets them from a server over a connection without encryption, so input made wrong on purpose is in scope:

- **Decompression:** a `.dbc` file that makes the decompressor read or write outside its buffers, never end, or use memory or disk out of proportion to what was asked of it.
- **DBF reading:** a header or a record that leads to access outside a buffer, a loop that does not end, or an allocation without a bound.
- **Paths:** a remote path, a name in a listing of the server or the name of a part that makes the client read or write outside the cache directory or outside the destination it was given.
- **Cache:** a way to have one entry of the cache served as another, or a partial or failed download served as a complete one, across threads or processes.

## What is not

- **The connection has no TLS.** The DATASUS server refuses it, and DbSus says so: see ["Security" in the README](https://github.com/euclydesuchoas/dbsus/blob/main/README.md#security). That belongs to the server and is not a defect to report here. The same goes for the CRC-32 of a `.dbc`, which detects accidental corruption and not deliberate tampering.
- What the files of DATASUS hold, a file that DATASUS changed or removed, and the server being slow or out of reach.
- An exception for a file that is not valid (`InvalidDataException`, for one) is the intended behavior.
- Without `CacheDirectory`, downloads go to the system temporary folder, which other users of a shared machine may be able to reach. On such a machine, set `CacheDirectory` to a folder that only you can write to.

A bug that is not a security matter goes in a public issue.
