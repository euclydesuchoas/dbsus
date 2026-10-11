# DATASUS paths and file names

This guide describes where DATASUS keeps the files that `DbSus.Client` knows by name, how they are named, and what is left out. Paths are relative to `/dissemin/publicos` (`DatasusClientOptions.RootPath`).

Everything here was observed on the DATASUS server in October 2026. DATASUS can change it at any time, and the catalog follows the server.

## Building a path

`DatasusCatalog` has one `DatasusDataset` per file type. A dataset builds the path of the file of one state and one period, without reaching the server:

```csharp
using System;
using DbSus.Client;

string path = DatasusCatalog.SihSus.RD.GetPath(DatasusUf.SP, DatasusPeriod.OfMonth(2024, 7));
Console.WriteLine(path);                      // /SIHSUS/200801_/Dados/RDSP2407.dbc

DatasusDataset deaths = DatasusCatalog.Get("SIM", "DO");   // the same as DatasusCatalog.Sim.DO
foreach (string candidate in deaths.GetCandidatePaths(DatasusUf.RR, DatasusPeriod.OfYear(2025)))
{
    Console.WriteLine(candidate);             // /SIM/CID10/DORES/DORR2025.dbc, then /SIM/PRELIM/DORES/DORR2025.dbc
}
```

- **Only the shape of the request is checked:** a month for a monthly file type, a year for a yearly one, a real state. Whether the file exists is never checked: DATASUS publishes late, leaves gaps, and does not have every state in every period. A path built here may name a file that is not on the server; opening it then fails with `FileNotFoundException`.
- **Only the folder layout in use now is built.** A period before the dataset's `FirstPeriod` fails with `ArgumentOutOfRangeException`; use the path as a string for it (see "Older layouts" below).
- **Only files of a state are built.** Files for the whole country are reached with the path as a string.
- **The name is built in upper case, with a lower-case extension.** The server lists some names in another case (see "Letter case" below).

## Finding and opening a table

The client finds a table on the server from the same three things, and opens it:

```csharp
using System;
using DbSus.Client;

await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });

// Final data only: one table, downloaded and read, whether it is one file or several parts.
using (var reader = await client.OpenReaderAsync(DatasusCatalog.SiaSus.PA, DatasusUf.MG, DatasusPeriod.OfMonth(2024, 7)))
{
    while (reader.Read()) { }
}

// Final or preliminary: ask the server where the table is, then decide.
DatasusTableInfo table = await client.FindTableAsync(DatasusCatalog.Sim.DO, DatasusUf.RR, DatasusPeriod.OfYear(2025));
Console.WriteLine($"{table.Path}: {table.Parts.Count} file(s), {table.Size} bytes, preliminary: {table.IsPreliminary}");
using (var reader = await client.OpenReaderAsync(table))
{
    while (reader.Read()) { }
}

// What a folder lists for a dataset, in a range of periods: one entry per table.
var tables = await client.ListTablesAsync(DatasusCatalog.Cnes.LT, DatasusPeriod.OfMonth(2024, 1), DatasusPeriod.OfMonth(2024, 12), DatasusUf.RR);
```

- **`OpenReaderAsync(dataset, uf, period)` opens final data only.** When only a preliminary table exists, it fails with a `FileNotFoundException` that says so. Preliminary data is read by choice: find the table with `FindTableAsync`, see `IsPreliminary`, and pass it to `OpenReaderAsync(table)`.
- **`FindTableAsync` is the reliable way to know whether a table exists.** It looks in the folder's listing and then asks the server for the file itself, the final one first and then the preliminary one, so a listing that is incomplete or out of date does not hide a table.
- **`ListTablesAsync` shows what the listing shows, and no more.** It costs one listing per folder and no other request. A listing that arrives incomplete while the server still reports it as complete omits tables without any error (a known limit, see [FTP troubleshooting](https://github.com/euclydesuchoas/dbsus/blob/main/docs/ftp-troubleshooting.md)), and listings are reused for `ListingCacheDuration` (10 minutes by default), so a table published in the meantime does not show until then. Use it to see what there is, and `FindTableAsync` to be sure about one table.
- **The path of a table is the name as the server lists it** (`DatasusTableInfo.Path`), in its real letter case. The cache is keyed by the remote path, so opening by dataset, state and period always uses that name, and a file is not downloaded twice under two spellings.

## The file types in the catalog

In the file names, `{CODE}` is the code of the file type, `{UF}` is the two-letter abbreviation of the state, `{YY}` and `{YYYY}` are the year with two or four digits, and `{MM}` is the month with two digits.

- **Official name** is the name under which DATASUS publishes the file type, in Portuguese and spelled as DATASUS spells it (`DatasusDataset.OfficialName`).
- **From** is the first period the catalog accepts (`DatasusDataset.FirstPeriod`): the start of the folder layout in use now, not the first period with data. CNES-LT, for example, has files from 2005-10 only.

One real file of each of the 26 file types was read with DbSus: decompressed, its CRC-32 checked, and every field of every record read. It was usually a small and old file, and some held a single record. That says nothing about other periods, and nothing about the `PRELIM` folders.

### SIASUS

Folder `/SIASUS/200801_/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From |
|---|---|---|---|---|---|
| SIASUS | PA | Produção Ambulatorial | state | monthly | 2008-01 |
| SIASUS | AD | APAC de Laudos Diversos | state | monthly | 2008-01 |
| SIASUS | AM | APAC de Medicamentos | state | monthly | 2008-01 |
| SIASUS | AQ | APAC de Quimioterapia | state | monthly | 2008-01 |
| SIASUS | AR | APAC de Radioterapia | state | monthly | 2008-01 |
| SIASUS | PS | Psicossocial | state | monthly | 2008-01 |
| SIASUS | ATD | APAC Tratamento Dialítico | state | monthly | 2008-01 |
| SIASUS | ACF | APAC Confeção de Fístula Arteriovenosa | state | monthly | 2008-01 |

### SIHSUS

Folder `/SIHSUS/200801_/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From |
|---|---|---|---|---|---|
| SIHSUS | RD | AIH Reduzida | state | monthly | 2008-01 |
| SIHSUS | SP | Serviços Profissionais | state | monthly | 2008-01 |
| SIHSUS | RJ | AIH Rejeitadas | state | monthly | 2008-01 |
| SIHSUS | ER | AIH Rejeitadas com código de erro | state | monthly | 2008-01 |

### CNES

One folder per file type, `/CNES/200508_/Dados/{CODE}`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From |
|---|---|---|---|---|---|
| CNES | LT | Leitos | state | monthly | 2005-08 |
| CNES | EQ | Equipamentos | state | monthly | 2005-08 |
| CNES | ST | Estabelecimentos | state | monthly | 2005-08 |
| CNES | PF | Profissional | state | monthly | 2005-08 |
| CNES | SR | Serviço Especializado | state | monthly | 2005-08 |
| CNES | HB | Habilitação | state | monthly | 2005-08 |
| CNES | DC | Dados Complementares | state | monthly | 2005-08 |
| CNES | EP | Equipes | state | monthly | 2005-08 |
| CNES | EF | Estabelecimento Filantrópico | state | monthly | 2005-08 |
| CNES | GM | Gestão e Metas | state | monthly | 2005-08 |
| CNES | IN | Incentivos | state | monthly | 2005-08 |
| CNES | RC | Regra Contratual | state | monthly | 2005-08 |

### SIM

Folder `/SIM/CID10/DORES`, and `/SIM/PRELIM/DORES` for preliminary years; file name `DO{UF}{YYYY}.dbc`.

| Source | Code | Official name | Coverage | Published | From |
|---|---|---|---|---|---|
| SIM | DO | Declarações de Óbito | state and country | yearly | 1996 |

### SINASC

Folder `/SINASC/1996_/Dados/DNRES`, and `/SINASC/PRELIM/DNRES` for preliminary years; file name `DN{UF}{YYYY}.dbc`.

| Source | Code | Official name | Coverage | Published | From |
|---|---|---|---|---|---|
| SINASC | DN | Declarações de nascidos vivos | state and country | yearly | 1996 |

## What to know about these files

- **Split tables.** In the listings of October 2026, SIASUS-PA was split into lettered parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, up to `d`) in five states: São Paulo in most months since 2011-12, Minas Gerais and Rio de Janeiro in every month since 2021-01, Rio Grande do Sul in some months since 2025-09, and Paraná from 2026-03 to 2026-05. `GetPath` gives the name without a part letter, `PAMG2407.dbc`; `FindTableAsync`, `FindPartsAsync` and `OpenReaderAsync` find the parts from it (see [downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md)).
- **Preliminary years.** SIM and SINASC publish the most recent years in a folder of preliminary data until they are closed. In the listings of October 2026, the final folders went from 1996 to 2024 and the preliminary ones held 2025 and 2026. Which years are preliminary changes, so the catalog does not know it: `GetCandidatePaths` gives the final path and then the preliminary one, and `FindTableAsync` tries them in that order.
- **Letter case.** The server lists most names in upper case with a lower-case extension, but not all: in the listings of October 2026, `DORR2011.DBC`, `DNRR2010.DBC`, `AQPA1001.DBC` and `sppa1001.dbc` were listed like that. `GetPath` always builds the name in upper case with a lower-case extension; the listing has the real case, which is what `FindTableAsync` and `ListTablesAsync` return. In October 2026 the server ignored case: a file listed as `DORR2011.DBC` answered under `dorr2011.dbc` too.
- **Gaps.** In the listings of October 2026, SIASUS-PA, SIHSUS RD and SP, CNES LT, EQ, ST, PF, HB, DC and EP, SIM-DO and SINASC-DN had no missing period between the first and the last file of each state. The other file types missed some state-months; SIASUS-AR had no file of Roraima, and CNES-EF none of Roraima or the Federal District. The latest months also arrive state by state: for several file types, the last month listed was not there for every state.
- **Files with no records.** Some files hold a header and no record at all, as two SIHSUS files of Acre in 2009-09 did in October 2026 (`SPAC0909.dbc`, `RJAC0909.dbc`). They open as an empty table.
- **Files that are not data.** The folders also hold a few other files, which are not part of any dataset: a placeholder named like `HBufAAmm.dbc` in five CNES folders, and two `.zip` files in the SIHSUS folder.
- **Size of the folders.** `/SIASUS/200801_/Dados` holds every SIASUS file type and listed 54,478 files; `/SIHSUS/200801_/Dados` listed 22,915; each CNES folder, up to 6,831.

## Older layouts

Periods before **From** are in other folders, some with other names. The catalog does not build them; use the path as a string.

| File type | Periods | Folder | File name |
|---|---|---|---|
| SIASUS-PA | 1994-07 to 2007-12 | `/SIASUS/199407_200712/Dados` | `PA{UF}{YY}{MM}.dbc` |
| SIHSUS-RD | 1992-01 to 2007-12 | `/SIHSUS/199201_200712/Dados` | `RD{UF}{YY}{MM}.dbc` |
| SIHSUS-SP | 1997-06 to 2007-12 | `/SIHSUS/199201_200712/Dados` | `SP{UF}{YY}{MM}.dbc` |
| SIHSUS-RJ | 2006-04 to 2007-12 | `/SIHSUS/199201_200712/Dados` | `RJ{UF}{YY}{MM}.dbc` |
| SIM-DO | 1979 to 1995 | `/SIM/CID9/DORES` | `DOR{UF}{YY}.dbc` |
| SINASC-DN | 1994 and 1995 | `/SINASC/1994_1995/Dados/DNRES` | `DNR{UF}{YYYY}.dbc` |

## Outside the catalog

These are on the server and are reached with the path as a string. Only their names were seen; none was read.

- **Files for the whole country.** SIM and SINASC have one more file per year with `BR` in place of the state (`DOBR2011.dbc`, `DNBR2010.dbc`), and SINASC has `DNEX{YYYY}.dbc` from 2014. SIM also has yearly files for the whole country in `/SIM/CID10/DOFET` (`DOFET`, `DOEXT`, `DOINF`, `DOMAT` and `DOREXT`, with a two-digit year). SIHSUS has `CH` and `CM` files with `BR` only.
- **SINAN.** Every file is for the whole country, with a two-digit year: `/SINAN/DADOS/FINAIS/DENGBR25.dbc`, and `/SINAN/DADOS/PRELIM` for preliminary years. The year in which a notifiable condition passes from preliminary to final differs from one condition to another.
- **SIASUS-BI.** Since 2023-05, in MG, RJ and SP, its files have a `_1`, `_2` or `_3` suffix (`BIMG2305_1.dbc`), sometimes next to a file without a suffix. DbSus does not treat them as parts of one table.
- **Other file types of the same folders.** SIASUS AB, ABO, AN, SAD and AMP, and CNES EE: few states, many gaps, or series that have ended.
- **Folders that look like leftovers.** `/SINASC/NOV` and `/SINASC/ANT` repeat names of the folders above, some with other sizes, and `/SIM/1996_` and `/SIM/1997_1995` are empty.

## Where the file transfer page and the server differ

The file transfer page of the DATASUS website states when some file types start or end. For these, the server had files outside those dates (observed in October 2026). The catalog follows the server, which is one more reason why it never checks availability.

| File type | The file transfer page says | The server has |
|---|---|---|
| SIASUS-PS | from January 2013 | files from 2012-11, for 6 states |
| SIASUS-ATD, SIASUS-ACF | from June 2014 | files from 2014-08 |
| CNES-GM | from June 2007 | files from 2007-03, for 16 states |
| CNES-IN | from November 2007 | files from 2007-10, for 10 states |
| SIASUS-AB (outside the catalog) | until March 2013 | files until 2025-07 |
| SIASUS-ABO (outside the catalog) | from April 2013 | files from 2014-01 |
