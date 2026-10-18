# Compatibility

Target frameworks, runtimes, dependencies, long paths on .NET Framework, the FluentFTP version used by `DbSus.Client`, and TLS on the DATASUS server.

## Target frameworks and runtimes

Both packages target `net10.0`, `net472` and `netstandard2.0`.

| | Runtimes |
|---|---|
| Supported | .NET 8 or later; .NET Framework 4.7.2 or later (the declared minimum; it gets the `net472` build); other `netstandard2.0` platforms |
| Tested | The test suite runs on .NET 10 and .NET Framework 4.8.1. A package test in CI also runs on .NET 8, with the `netstandard2.0` build. |

The supported runtimes are the ones the packages are built for, whether or not Microsoft still supports them; Microsoft's end of support for .NET 8 and .NET 9 is in November 2026.

The handling of stalled reads in `DbSus.Client` was verified on .NET 10 and .NET Framework 4.8.1; .NET 8 and 9, Mono, Unity and Xamarin were not checked.

The code examples in the guides compile with C# 8. The optional comparison on raw bytes with a `u8` literal needs C# 11.

The `net10.0` build of `DbSus` is marked as compatible with trimming and Native AOT (`IsAotCompatible`). In CI, an application that uses only `DbSus` is published trimmed on Linux and Windows (x64) and with Native AOT on Windows (x64), with no trimming or AOT warnings, and its output is the same as without trimming. It reads `.dbc` and `.dbf` data built in memory: decompression with the three dictionary sizes, coded and uncoded literals, the CRC-32 check, typed values, the schema table, and a code page taken from the language driver ID.

Not covered: Native AOT on other platforms, real DATASUS files, and `DataTable.Load`, which is not compatible with trimming because of System.Data. The `netstandard2.0` build, which .NET 8 and .NET 9 applications use, and `DbSus.Client` are not marked.

## Dependencies

Dependencies declared by each package, per target. Each version is a minimum: an application that already references one of them, at that version or later, keeps its own version without a downgrade error (NU1605). The one upper bound is that of `DbSus` in `DbSus.Client`, which stops before the next minor version and its previews: an application that references a `DbSus` of a later minor version gets warning NU1608, and the restore fails (NU1107) when another dependency asks for one.

| Package | `net10.0` | `net472` | `netstandard2.0` |
|---|---|---|---|
| `DbSus` | None | `System.Memory` 4.5.3 | `System.Memory` 4.5.3, `System.Text.Encoding.CodePages` 4.4.0 |
| `DbSus.Client` | `DbSus` (from the same version, before the next minor and its previews), FluentFTP 55.0.0 | `DbSus` (from the same version, before the next minor and its previews), FluentFTP 55.0.0, `Microsoft.Bcl.AsyncInterfaces` 1.0.0 | `DbSus` (from the same version, before the next minor and its previews), FluentFTP 55.0.0, `Microsoft.Bcl.AsyncInterfaces` 1.0.0 |

For FluentFTP, see below.

## .NET Framework and System.Memory

.NET Framework 4.7.2 or later uses the packages' `net472` build. If an application still fails on the first read with a `FileLoadException` for `System.Memory` (for example an unusual build setup that resolves the `netstandard2.0` build instead), add a binding redirect to its `app.config`. `newVersion` must be the assembly version of the `System.Memory.dll` the application ships; for example 4.0.5.0, from the `System.Memory` 4.6.3 package:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />
</dependentAssembly>
```

## Long paths on .NET Framework

On .NET Framework without long path support, a file whose path is longer than 260 characters cannot be created. A cache entry adds the remote path, a version folder and the file name to `CacheDirectory`, about 80 characters for DATASUS files (for example `\SIASUS\200801_\Dados\PAMG2407a.dbc\183994941-20250808134958\PAMG2407a.dbc.partial`), so keep `CacheDirectory` short, such as `D:\datasus-cache`. When an entry cannot be written, `DownloadAsync` and `OpenReaderAsync` throw an `IOException` that says so, with the original exception as its inner exception, before the file's data is requested. The failure was not observed on .NET 10. See [Maximum Path Length Limitation](https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation) in the Windows documentation.

## FluentFTP

`DbSus.Client` is tested with FluentFTP 55.0.0 and declares that version as a minimum. NuGet picks the lowest version that satisfies every requirement, so an application gets FluentFTP 55.0.0 unless it references a newer version itself or another package requires one.

On .NET Framework, FluentFTP is strong-named and its assembly version changes with every release, so any version other than 55.0.0 needs a binding redirect: SDK-style executable projects generate it by default; ASP.NET (`web.config`) and older projects need it written by hand.

### Data connections

`DbSus.Client` uses FluentFTP's `PASVEX` mode: it sends `PASV`, ignores the address in the reply and connects to `Host`, on the port the reply names. It never uses FluentFTP's `AutoPassive` mode, so the change FluentFTP 55.1.0 made to it (no fallback from `EPSV` to `PASV` when the server lists `EPSV` in its `FEAT` reply and then refuses it) does not affect the client. `PASVEX` behaves the same in FluentFTP 55.0.0 and 55.1.0 (tested against a loopback server, October 2026).

- Over IPv6, FluentFTP sends `EPSV` in place of `PASVEX`, since `PASV` carries only an IPv4 address. Networks with IPv6 only, with a server that does not support `EPSV`, do not work.
- A data connection is made to `Host` again, not to the address the control connection uses: the addresses of `Host` (which FluentFTP looks up once for each connection and reuses) are tried in order, from the first, so with a name that has several addresses a data connection can start with an address other than the one the control connection uses.
- A server that sends its data from an address other than those of `Host` is not supported.

### What a newer FluentFTP can change

The client uses FluentFTP's protected connection hooks to close a connection by force. An experiment with stand-in libraries, on .NET 10 and .NET Framework 4.8.1, covered two ways a version could lose them: removing a hook (a virtual method) and removing the type of its parameter. Neither failed to build or load; the client's overrides were simply no longer called, as when a version keeps the hooks but stops calling them. Other changes, such as a removed constructor or a sealed client class, can make the client fail when it loads or first uses FluentFTP (`MissingMethodException`, `TypeLoadException`). The weekly workflow described below is what detects such changes.

- **On .NET Framework**, ending a stalled or canceled operation depends on those hooks, so the client checks that FluentFTP reported each connection. If the control connection was not reported, connecting fails. If a data connection was not reported, opening a file for reading fails before anything is read, and a listing fails right after FluentFTP has read it (its result is discarded). The error is a `NotSupportedException` that names the FluentFTP version in use and the one the client is tested with; it is not retried.
- **On the other runtimes** there is no such check. On .NET 10, the only one of them where this was tested, canceling, the read timeout and the limit on each read of a download still ended stalled operations with the hooks switched off in a test.

A test of the hooks watches for such a change, and a weekly CI workflow runs the client's tests against the newest stable FluentFTP, so that a release that changes how the hooks are called shows up as a failing run. It only reports; it does not stop an application from using that release.

## The DATASUS server

`DbSus.Client` connects without encryption because the server refuses TLS: its `FEAT` reply lists `AUTH TLS`, but in October 2026 `AUTH TLS` was answered with `534 Local policy on server does not allow TLS secure connections.` See Security in the [README](https://github.com/euclydesuchoas/dbsus/blob/main/README.md).
