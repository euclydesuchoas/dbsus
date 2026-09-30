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

## Allocation-free reading

`DbfReader.Read` and the typed getters (`GetInt32`, `GetInt64`, `GetDecimal`, `GetDouble`, `GetDateTime`, `GetBoolean`, `IsDBNull`, `GetRawBytes`) do not allocate per record. `GetFieldValue<T>` does not allocate either on .NET 5 and later. **On .NET Framework, its nullable forms (e.g. `GetFieldValue<int?>`) allocate about 24 bytes per call** (measured on 4.8.1, x64; the non-nullable forms do not), so there prefer `IsDBNull` plus a typed getter in hot loops. `GetValue`, `GetValues` and `GetString` allocate by nature.

## Integrity is confirmed only at the end

Every `.dbc` file stores a CRC-32 of the whole DBF it contains. That checksum can only be compared once the last byte has been decompressed, so DbSus reports a mismatch **at the end of the data**, not at the start:

- With `DbSusFile.OpenDbcReader` (or `OpenReader` on a `.dbc`), the `Read()` call that would return `false` throws `InvalidDataException` instead. For a table split into parts, each part is checked when its last record has been read.
- With `DbcFile.OpenAsDbf`, the read that reaches the end of the stream throws.
- A reader disposed before the end never performs the check.

Every record returned before that point came from the same file, so if the checksum fails they cannot be trusted either. **When loading into a database, write inside a transaction or into a staging table, and commit (or swap the staging table in) only after `Read()` has returned `false` without an exception.**

```csharp
using var reader = DbSusFile.OpenDbcReader(DbSusFile.FindParts("PAMG2407a.dbc"));
using var transaction = connection.BeginTransaction();
using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "staging_pa" };
bulk.WriteToServer(reader);   // throws if any part's checksum does not match
transaction.Commit();         // reached only when every part was read and verified
```

## License

[MIT](LICENSE).
