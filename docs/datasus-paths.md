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

In the file name patterns below, `{UF}` is the two-letter abbreviation of the state, `{YY}` and `{YYYY}` are the year with two or four digits, and `{MM}` is the month with two digits.

## The file types in the catalog

| Source | Code | Official name | Coverage | Published | From | Folder | File name | Read with DbSus |
|---|---|---|---|---|---|---|---|---|
| SIASUS | PA | Produção Ambulatorial | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `PA{UF}{YY}{MM}.dbc` | yes |
| SIASUS | AD | APAC de Laudos Diversos | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `AD{UF}{YY}{MM}.dbc` | no |
| SIASUS | AM | APAC de Medicamentos | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `AM{UF}{YY}{MM}.dbc` | no |
| SIASUS | AQ | APAC de Quimioterapia | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `AQ{UF}{YY}{MM}.dbc` | no |
| SIASUS | AR | APAC de Radioterapia | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `AR{UF}{YY}{MM}.dbc` | no |
| SIASUS | PS | Psicossocial | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `PS{UF}{YY}{MM}.dbc` | no |
| SIASUS | ATD | APAC Tratamento Dialítico | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `ATD{UF}{YY}{MM}.dbc` | no |
| SIASUS | ACF | APAC Confeção de Fístula Arteriovenosa | state | monthly | 2008-01 | `/SIASUS/200801_/Dados` | `ACF{UF}{YY}{MM}.dbc` | no |
| SIHSUS | RD | AIH Reduzida | state | monthly | 2008-01 | `/SIHSUS/200801_/Dados` | `RD{UF}{YY}{MM}.dbc` | yes |
| SIHSUS | SP | Serviços Profissionais | state | monthly | 2008-01 | `/SIHSUS/200801_/Dados` | `SP{UF}{YY}{MM}.dbc` | no |
| SIHSUS | RJ | AIH Rejeitadas | state | monthly | 2008-01 | `/SIHSUS/200801_/Dados` | `RJ{UF}{YY}{MM}.dbc` | no |
| SIHSUS | ER | AIH Rejeitadas com código de erro | state | monthly | 2008-01 | `/SIHSUS/200801_/Dados` | `ER{UF}{YY}{MM}.dbc` | no |
| CNES | LT | Leitos | state | monthly | 2005-08 | `/CNES/200508_/Dados/LT` | `LT{UF}{YY}{MM}.dbc` | yes |
| CNES | EQ | Equipamentos | state | monthly | 2005-08 | `/CNES/200508_/Dados/EQ` | `EQ{UF}{YY}{MM}.dbc` | yes |
| CNES | ST | Estabelecimentos | state | monthly | 2005-08 | `/CNES/200508_/Dados/ST` | `ST{UF}{YY}{MM}.dbc` | yes |
| CNES | PF | Profissional | state | monthly | 2005-08 | `/CNES/200508_/Dados/PF` | `PF{UF}{YY}{MM}.dbc` | no |
| CNES | SR | Serviço Especializado | state | monthly | 2005-08 | `/CNES/200508_/Dados/SR` | `SR{UF}{YY}{MM}.dbc` | no |
| CNES | HB | Habilitação | state | monthly | 2005-08 | `/CNES/200508_/Dados/HB` | `HB{UF}{YY}{MM}.dbc` | no |
| CNES | DC | Dados Complementares | state | monthly | 2005-08 | `/CNES/200508_/Dados/DC` | `DC{UF}{YY}{MM}.dbc` | no |
| CNES | EP | Equipes | state | monthly | 2005-08 | `/CNES/200508_/Dados/EP` | `EP{UF}{YY}{MM}.dbc` | no |
| CNES | EF | Estabelecimento Filantrópico | state | monthly | 2005-08 | `/CNES/200508_/Dados/EF` | `EF{UF}{YY}{MM}.dbc` | no |
| CNES | GM | Gestão e Metas | state | monthly | 2005-08 | `/CNES/200508_/Dados/GM` | `GM{UF}{YY}{MM}.dbc` | no |
| CNES | IN | Incentivos | state | monthly | 2005-08 | `/CNES/200508_/Dados/IN` | `IN{UF}{YY}{MM}.dbc` | no |
| CNES | RC | Regra Contratual | state | monthly | 2005-08 | `/CNES/200508_/Dados/RC` | `RC{UF}{YY}{MM}.dbc` | no |
| SIM | DO | Declarações de Óbito | state and country | yearly | 1996 | `/SIM/CID10/DORES`, preliminary in `/SIM/PRELIM/DORES` | `DO{UF}{YYYY}.dbc` | no |
| SINASC | DN | Declarações de nascidos vivos | state and country | yearly | 1996 | `/SINASC/1996_/Dados/DNRES`, preliminary in `/SINASC/PRELIM/DNRES` | `DN{UF}{YYYY}.dbc` | no |

- **Official name** is the name under which DATASUS publishes the file type, in Portuguese and spelled as DATASUS spells it (`DatasusDataset.OfficialName`).
- **From** is the first period the catalog accepts (`DatasusDataset.FirstPeriod`): the start of the folder layout in use now, not the first period with data. CNES-LT, for example, has files from 2005-10 only.
- **Read with DbSus** is "yes" when a real file of that type was decompressed, passed its CRC-32 check and was read with DbSus, record by record. For the others, only the path was verified: files with those names are on the server, and none was read. Among them, a file of each of SIASUS AD, AM, AQ, AR and PS, SIHSUS SP, RJ and ER, CNES PF, SR and HB, SIM DO and SINASC DN was decompressed with DbSus in September 2026, with a result identical to that of a reference decoder; those files were not kept and were not read record by record, so they stay "no". No file from the `PRELIM` folders was read.

## What to know about these files

- **Split tables.** SIASUS-PA is split into parts in the larger states (MG, PR, RJ, RS and SP, since 2011-12): `PAMG2407a.dbc`, `PAMG2407b.dbc`, up to `d`. `GetPath` gives the name without a part letter, `PAMG2407.dbc`, which `DatasusClient.FindPartsAsync` and `DatasusClient.OpenReaderAsync` accept (see [downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md)).
- **Preliminary years.** SIM and SINASC publish the most recent years in a folder of preliminary data until they are closed; in October 2026 those were 2025 and 2026. Which years are preliminary changes, so the catalog does not know it: `GetCandidatePaths` gives the final path and then the preliminary one, to be tried in that order.
- **Letter case.** The server lists most names in upper case with a lower-case extension, but not all: `DORR2011.DBC`, `DNRR2010.DBC`, `AQPA1001.DBC` and `sppa1001.dbc` are listed like that. The paths built here are always upper case with a lower-case extension. In October 2026 the server ignored case: a file listed as `DORR2011.DBC` answered under `dorr2011.dbc` too.
- **Gaps.** Only SIASUS-PA, SIHSUS RD and SP, CNES LT, EQ, ST, PF, HB, DC and EP, SIM-DO and SINASC-DN had no missing period between the first and the last file of each state. The other file types miss some state-months, and SIASUS-AR has no file of Roraima.
- **Files that are not data.** The folders also hold a few other files, which are not part of any dataset: a placeholder named like `HBufAAmm.dbc` in five CNES folders, two `.zip` files in the SIHSUS folder and a `.DLL` in the SIASUS one.
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
