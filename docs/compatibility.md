# Compatibility

Target frameworks, runtimes, dependencies and the FluentFTP version used by `DbSus.Client`.

## Target frameworks and runtimes

Both packages target `net10.0`, `net472` and `netstandard2.0`.

| | Runtimes |
|---|---|
| Supported | .NET 8 or later; .NET Framework 4.7.2 or later (the declared minimum; it gets the `net472` build); other `netstandard2.0` platforms |
| Tested | The test suite runs on .NET 10 and .NET Framework 4.8.1. A package test in CI also runs on .NET 8, with the `netstandard2.0` build. |

The handling of stalled reads in `DbSus.Client` was verified on .NET 10 and .NET Framework 4.8; Mono, Unity and Xamarin were not checked.

The code examples in the guides compile with C# 8. The optional comparison on raw bytes with a `u8` literal needs C# 11.

## Dependencies

The packages accept `System.Memory` 4.5.3 or later, `System.Text.Encoding.CodePages` 4.4.0 or later and `Microsoft.Bcl.AsyncInterfaces` 1.0.0 or later, so an application that already references one of them, at those versions or later, keeps its own version without a downgrade error (NU1605). `DbSus` has no dependency on `net10.0`; `DbSus.Client` also depends on FluentFTP (see below).

## .NET Framework and System.Memory

.NET Framework 4.7.2 or later uses the packages' `net472` build. If an application still fails on the first read with a `FileLoadException` for `System.Memory` (for example an unusual build setup that resolves the `netstandard2.0` build instead), add a binding redirect to its `app.config`. `newVersion` must be the assembly version of the `System.Memory.dll` the application ships; for example 4.0.5.0, from the `System.Memory` 4.6.3 package:

```xml
<dependentAssembly>
  <assemblyIdentity name="System.Memory" publicKeyToken="cc7b13ffcd2ddd51" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-4.0.5.0" newVersion="4.0.5.0" />
</dependentAssembly>
```

## FluentFTP

`DbSus.Client` is tested with FluentFTP 55.0.0 and declares that version as a minimum. NuGet picks the lowest version that satisfies every requirement, so an application gets FluentFTP 55.0.0 unless it references a newer version itself or another package requires one.

On .NET Framework, FluentFTP is strong-named and its assembly version changes with every release, so any version other than 55.0.0 needs a binding redirect: SDK-style executable projects generate it; ASP.NET (`web.config`) and older projects need it written by hand.

### What a newer FluentFTP can change

The client uses FluentFTP's protected connection hooks to close a connection by force. If a version removes them or stops calling them, nothing fails to build or load.

- **On .NET Framework**, ending a stalled or canceled operation depends on those hooks, so the client checks that FluentFTP reported each connection. If the control connection was not reported, connecting fails. If a data connection was not reported, opening a file for reading fails before anything is read, and a listing fails right after FluentFTP has read it (its result is discarded). The error is a `NotSupportedException` that names the FluentFTP version in use and the one the client is tested with; it is not retried.
- **On the other runtimes** there is no such check: there, with the hooks switched off in a test, canceling, the read timeout and the limit on each read of a download still ended stalled operations.

A test of the hooks watches for such a change, and a weekly CI workflow runs the client's tests against the newest stable FluentFTP, so that a release that changes how the hooks are called shows up as a failing run. It only reports; it does not stop an application from using that release.
