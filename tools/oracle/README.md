# DBC oracle

Development-only reference for DBC → DBF conversion. It runs `pyreaddbc` (AGPL-3.0) inside Docker as a black box to produce expected hashes; `pyreaddbc` is never a dependency of DbSus and its source is not used as a basis for DbSus code.

## Usage

```sh
docker build -t dbsus-oracle tools/oracle
docker run --rm -v <folder with .dbc files>:/data:ro dbsus-oracle /data > oracle.json
```

The output lists, for each `.dbc`, the SHA-256 and size of the `.dbc` itself (pinning the exact version, since DATASUS republishes files) and of the `.dbf` the oracle produces. Conversions happen in a temporary directory inside the container; nothing is written to the host except what you redirect.

Entries are then copied into `tests/data/expected.json` by hand, adding the `source` FTP path for files `tools/fetch-samples` should download.

## When the oracle is not the reference

The CRC-32 stored in every DBC covers the whole original DBF, so it is the final arbiter. The oracle is known to differ from it in one case:

- **`STRR2301.dbc`** (CNES establishments, Roraima, 2023-01) has 0x00 as the last header byte, where a 0x0D field terminator is normally found. DbSus preserves the byte and its output matches the stored CRC-32; `pyreaddbc` rewrites it to 0x0D and its output does not match. The expected hash for this file therefore comes from DbSus, validated by the CRC, and the entry in `expected.json` says so in `dbfHashOrigin`.

Before accepting an oracle hash for a new file, check that the oracle's DBF matches the CRC-32 stored in the DBC (bytes `[H, H + 4)`, little-endian, where `H` is the header length at offset 8).
