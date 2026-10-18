# Contributing

Issues and pull requests are welcome. For a vulnerability, see the [security policy](https://github.com/euclydesuchoas/dbsus/blob/main/SECURITY.md) instead of opening an issue.

## Building and testing

The .NET SDK 10 is needed (the version is in `global.json`). From the root of the repository:

```shell
dotnet restore DbSus.slnx
dotnet build DbSus.slnx --configuration Release --no-restore
dotnet test --solution DbSus.slnx --configuration Release --no-build
```

The tests run on .NET 10 and, on Windows, also on .NET Framework 4.8.1. The build treats warnings as errors. Tests that need large samples skip themselves when the files are not there; nothing has to be downloaded to run the suite.

The `csharp` blocks of `README.md` and of `docs/*.md` are compiled, and most of them are run. After changing one:

```shell
dotnet run tools/doc-examples/generate.cs
dotnet build tests/doc-examples --configuration Release
dotnet run --project tests/doc-examples --configuration Release --no-build --framework net10.0
```

## Data

No data of DATASUS goes into the repository: its files can hold personal data. The only ones committed are the two small tables described in `tests/data/fixtures/README.md`, and `.gitignore` refuses any other `.dbc` or `.dbf`. Write a test with a file built in memory, as the existing ones do. In an issue, give the path of the file on the server instead of attaching it, and do not paste the content of records.

## Commits and pull requests

- [Conventional Commits](https://www.conventionalcommits.org/), in English, in one line: `fix: refuse a month whose two-digit year would name an earlier one`.
- One subject per commit, with its tests.
- The CI has to pass: build and tests on Ubuntu and on Windows, the package consumers, the trimming and Native AOT check, the documentation examples and the CodeQL analysis.

## Public API

Each library lists its public API in `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, and the build fails when the code and the files disagree. A change to the public API comes with its lines in `PublicAPI.Unshipped.txt` of the project, and with an entry in `CHANGELOG.md` under "Unreleased". During 0.x a minor version may break the API; say so in the pull request when yours does.

What was decided, and why, is in [`docs/decisions.md`](https://github.com/euclydesuchoas/dbsus/blob/main/docs/decisions.md).
