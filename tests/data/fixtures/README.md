# Committed DBC fixtures

The only DATASUS files committed to this repository. Every other sample is fetched into `tests/data/local/` by `tools/fetch-samples` and never committed, because DATASUS microdata can hold personal data.

These two files are small, publicly distributed CNES tables with **establishment-level data only**: every record has `PF_PJ = 3` (legal entity), and the fields are CNES codes, municipality codes, administrative classifications, CNPJs of legal entities and equipment or bed counts. No field identifies a natural person. All records were inspected before committing (2026-09-30).

| File | Content | Origin (ftp.datasus.gov.br) | Size | SHA-256 |
|---|---|---|---|---|
| `EQRR0509.dbc` | CNES equipment, Roraima, 2005-09 (27 records) | `/dissemin/publicos/CNES/200508_/Dados/EQ/EQRR0509.dbc` | 1,213 bytes | `80fbe66d395c215fa25ded25259a9d47bc0d44ef53371f92822eab765b1bb34c` |
| `LTRR0610.dbc` | CNES hospital beds, Roraima, 2006-10 (102 records) | `/dissemin/publicos/CNES/200508_/Dados/LT/LTRR0610.dbc` | 2,065 bytes | `00a404b42829c26770bc5de0eac16714cff91c72b241f2df8f5b624cee24f0d7` |

The expected DBF hashes (from the `pyreaddbc` oracle) are in `tests/data/expected.json`. The files are allowed by an explicit negation rule in `.gitignore` (`!tests/data/fixtures/*.dbc`).

Decoder paths these fixtures exercise, and the ones they do not, are recorded in `docs/decisions.md`.

DbSus is an independent project, not affiliated with the Brazilian Ministry of Health or DATASUS.
