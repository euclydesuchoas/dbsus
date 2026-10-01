# Package consumer

An outside application that restores the packed `DbSus` and `DbSus.Client` packages and checks, per target framework, which build it loaded and that it can read the committed CNES fixture (typed getters, `GetFieldValue<int?>`, a conversion, DBC → DBF, a split table) and load the client and FluentFTP. No network. It is not part of `DbSus.slnx` and does not use the repository's build settings.

| Target | Package build it exercises |
|---|---|
| `net8.0` | `netstandard2.0` |
| `net10.0` | `net10.0` |
| `net481` (Windows only) | `net472` |

`sdk8/` builds the same program for `net8.0` with SDK 8 (its own `global.json`), which, unlike SDK 10, does not prune packages that are part of the framework from the restore.

## Running it locally

The package version is required: there is no default, so a run never picks up an older package with the same number from a NuGet cache. Local packs use a `local` suffix with a timestamp, which sorts before any `preview` version:

```shell
dotnet pack DbSus.slnx --configuration Release --output artifacts/packages -p:VersionSuffix=local.202610011530
dotnet run --project tests/package-consumer --configuration Release --framework net10.0 -p:DbSusVersion=0.1.0-local.202610011530
```

The packages are restored from `artifacts/packages` into `artifacts/consumer-packages` (see `nuget.config`), not into the global NuGet cache.

Other runs, as in CI:

```shell
# An application that already references the oldest accepted dependency versions.
dotnet run --project tests/package-consumer --configuration Release --framework net481 -p:DbSusVersion=<version> -p:PinOldestDependencies=true

# The net8.0 consumer built with SDK 8 (run from that folder so its global.json applies).
cd tests/package-consumer/sdk8
dotnet run --configuration Release -p:DbSusVersion=<version>
```

Without `-p:DbSusVersion`, the build stops with an error that names the property.
