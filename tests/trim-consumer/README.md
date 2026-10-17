# Trim consumer

An outside application that uses only the packed `DbSus` package, for the "Trimming and AOT" job of the CI. The job runs it normally, then publishes it trimmed (Linux and Windows, x64) and, on Windows, with Native AOT, runs the published applications and compares their output with that of the normal run. It is not part of `DbSus.slnx` and does not use the repository's build settings.

Every warning fails the build and the publish, those of the trimmer and of the Native AOT compiler included, whether they come from `DbSus` or from this program; only the NuGet vulnerability warnings (NU1901 to NU1904) stay warnings. `DbSus.Client` is not marked as compatible with trimming or Native AOT and is left out.

Every input is built in memory, with the test helpers of `tests/DbSus.Tests/TestSupport` (linked, not copied): DBC files with uncoded literals only, with copies over the three dictionary sizes and coded or uncoded literals, and with a CRC-32 that does not match; a table in code page 850 read with typed getters, a conversion and the schema table; and a conversion to a DBF file on disk. No DATASUS file is read. The output has only numbers and type names, so that the runs can be compared.

## Running it locally

The package version is required, as for `tests/package-consumer`:

```shell
dotnet pack src/DbSus/DbSus.csproj --configuration Release --output artifacts/packages -p:VersionSuffix=local.202610011530
dotnet run --project tests/trim-consumer --configuration Release -p:DbSusVersion=0.1.0-local.202610011530
dotnet publish tests/trim-consumer --configuration Release --runtime win-x64 --self-contained -p:PublishTrimmed=true -p:DbSusVersion=0.1.0-local.202610011530 --output artifacts/trim-published
```

`-p:PublishAot=true` in place of `-p:PublishTrimmed=true` publishes with Native AOT, which on Windows needs the MSVC C++ build tools of Visual Studio.

The package is restored from `artifacts/packages` into `artifacts/trim-consumer-packages` (see `nuget.config`), not into the global NuGet cache.
