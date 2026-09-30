# DbSus

A .NET library for reading the `.dbc` (compressed dBase) and `.dbf` files distributed by DATASUS, in fully managed C#, with no dependency on R, Python or native binaries.

> **Disclaimer:** DbSus is an independent project. It is not affiliated with, endorsed by or maintained by the Brazilian Ministry of Health (Ministério da Saúde) or DATASUS.

## Status

Early development. Nothing is published on NuGet yet and the API is not stable.

## Planned packages

| Package | Purpose |
|---|---|
| `DbSus` | Core: DBC decompression and DBF reading (streaming, typed getters, `DbDataReader`). |
| `DbSus.Client` | Direct access to the DATASUS file server, chaining download, decompression and reading. |

Target frameworks: `netstandard2.0` and `net10.0`.

## License

[MIT](LICENSE).
