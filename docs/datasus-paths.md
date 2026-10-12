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
- **Older folder layouts are built for six file types.** For SIASUS-PA, SIHSUS RD, SP and RJ, SIM-DO and SINASC-DN, a period before the layout in use now gets the folder and the file name it had then (see "Older layouts" below). A period before the dataset's `FirstPeriod` fails with `ArgumentOutOfRangeException`.
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
- **From** is the first period the catalog accepts (`DatasusDataset.FirstPeriod`): the start of the oldest folder layout it knows for the file type (see "Older layouts" below), not the first period with data. CNES-LT, for example, has files from 2005-10 only, SIASUS-ABO from 2014-01 and SIASUS-AMP from 2016-03.

The tables below say, for each of the 40 file types, what was checked in October 2026: `read` means that one real file was read whole with DbSus (decompressed, its CRC-32 checked, every field of every record), usually a small and old one, and some held a single record; `header`, that a header alone was read, or that the file read had no record; `names`, that only the names were seen in a listing. From the `PRELIM` folders, two files were read in the same way in October 2026: SIM-DO and SINASC-DN of Roraima, both of 2026. That says nothing about other periods, or about other files of those folders.

### SIASUS

Folder `/SIASUS/200801_/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

SIASUS-PA is in this folder from 2008-01 on; for earlier months, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIASUS | PA | Produção Ambulatorial | state | monthly | 1994-07 | read |
| SIASUS | AD | APAC de Laudos Diversos | state | monthly | 2008-01 | read |
| SIASUS | AM | APAC de Medicamentos | state | monthly | 2008-01 | read |
| SIASUS | AQ | APAC de Quimioterapia | state | monthly | 2008-01 | read |
| SIASUS | AR | APAC de Radioterapia | state | monthly | 2008-01 | read |
| SIASUS | PS | Psicossocial | state | monthly | 2008-01 | read |
| SIASUS | ATD | APAC Tratamento Dialítico | state | monthly | 2008-01 | read |
| SIASUS | ACF | APAC Confeção de Fístula Arteriovenosa | state | monthly | 2008-01 | read |
| SIASUS | AB | APAC de Acompanhamento a Cirurgia Bariátrica | state | monthly | 2008-01 | read |
| SIASUS | ABO | APAC Acompanhamento Pós Cirurgia Bariátrica | state | monthly | 2008-01 | read |
| SIASUS | AN | APAC de Nefrologia | state | monthly | 2008-01 | read |
| SIASUS | SAD | Atenção Domiciliar | state | monthly | 2008-01 | read |
| SIASUS | AMP | Laudo de Acompanhamento Multiprofissional | state | monthly | 2008-01 | read |

The name of SIASUS-AMP comes from a document in the SIASUS folder of the server; the file transfer page of the DATASUS website does not list this file type.

### SIHSUS

Folder `/SIHSUS/200801_/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

SIHSUS RD, SP and RJ are in this folder from 2008-01 on; for earlier months, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIHSUS | RD | AIH Reduzida | state | monthly | 1992-01 | read |
| SIHSUS | SP | Serviços Profissionais | state | monthly | 1992-01 | read |
| SIHSUS | RJ | AIH Rejeitadas | state | monthly | 1992-01 | read |
| SIHSUS | ER | AIH Rejeitadas com código de erro | state | monthly | 2008-01 | read |

### CNES

One folder per file type, `/CNES/200508_/Dados/{CODE}`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| CNES | LT | Leitos | state | monthly | 2005-08 | read |
| CNES | EQ | Equipamentos | state | monthly | 2005-08 | read |
| CNES | ST | Estabelecimentos | state | monthly | 2005-08 | read |
| CNES | PF | Profissional | state | monthly | 2005-08 | read |
| CNES | SR | Serviço Especializado | state | monthly | 2005-08 | read |
| CNES | HB | Habilitação | state | monthly | 2005-08 | read |
| CNES | DC | Dados Complementares | state | monthly | 2005-08 | read |
| CNES | EP | Equipes | state | monthly | 2005-08 | read |
| CNES | EF | Estabelecimento Filantrópico | state | monthly | 2005-08 | read |
| CNES | GM | Gestão e Metas | state | monthly | 2005-08 | read |
| CNES | IN | Incentivos | state | monthly | 2005-08 | read |
| CNES | RC | Regra Contratual | state | monthly | 2005-08 | read |
| CNES | EE | Estabelecimento de Ensino | state | monthly | 2005-08 | read |

### SIM

From 1996 on: folder `/SIM/CID10/DORES`, and `/SIM/PRELIM/DORES` for preliminary years; file name `DO{UF}{YYYY}.dbc`.

For 1979 to 1995, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIM | DO | Declarações de Óbito | state and country | yearly | 1979 | read |

### SINASC

From 1996 on: folder `/SINASC/1996_/Dados/DNRES`, and `/SINASC/PRELIM/DNRES` for preliminary years; file name `DN{UF}{YYYY}.dbc`.

For 1994 and 1995, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SINASC | DN | Declarações de nascidos vivos | state and country | yearly | 1994 | read |

### CIHA

Folder `/CIHA/201101_/Dados`, file name `CIHA{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| CIHA | CIHA | Comunicação de Internação Hospitalar e Ambulatorial | state | monthly | 2011-01 | read |

### CIH

Folder `/CIH/200801_201012/Dados`, file name `CR{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| CIH | CR | Comunicação de Internação Hospitalar | state | monthly | 2008-01 | read |

### SISPRENATAL

Folder `/SISPRENATAL/201201_/Dados`, file name `PN{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SISPRENATAL | PN | Pré-Natal | state | monthly | 2012-01 | read |

### SISCOLO

Folder `/SISCAN/SISCOLO4/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SISCOLO | CC | Citopatológico de Colo de Útero | state | monthly | 2006-01 | read |
| SISCOLO | HC | Histopatológico de Colo de Útero | state | monthly | 2006-01 | read |

### SISMAMA

Folder `/SISCAN/SISMAMA/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SISMAMA | CM | Citopatológico de Mama | state | monthly | 2009-01 | read |
| SISMAMA | HM | Histopatológico de Mama | state | monthly | 2009-01 | read |
| SISMAMA | MM | Exame Mamografia | state | monthly | 2009-01 | read |

The folders of SISCOLO and SISMAMA are inside `/SISCAN` and have no date in their names: **From** is the first month with files in the listings of October 2026. The name of SISMAMA-MM comes from a definition file in the SISMAMA folder of the server (`MMAMA4.def`, in `/SISCAN/SISMAMA/Auxiliar/TAB_SISMAMA.zip`); the file transfer page of the DATASUS website does not list this file type. That page labels SISMAMA-HM as "HC"; its files are named `HM`. Of each of the five file types of SISCOLO and SISMAMA, a small file opened earlier in October 2026 had a header and no record; another one, with records, was then read whole.

## What to know about these files

- **Split tables.** In the listings of October 2026, SIASUS-PA was split into lettered parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, up to `d`) in five states: São Paulo in most months since 2011-12, Minas Gerais and Rio de Janeiro in every month since 2021-01, Rio Grande do Sul in some months since 2025-09, and Paraná from 2026-03 to 2026-05. `GetPath` gives the name without a part letter, `PAMG2407.dbc`; `FindTableAsync`, `FindPartsAsync` and `OpenReaderAsync` find the parts from it (see [downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md)).
- **Preliminary years.** SIM and SINASC publish the most recent years in a folder of preliminary data until they are closed. In the listings of October 2026, the final folders went from 1996 to 2024 and the preliminary ones held 2025 and 2026. Which years are preliminary changes, so the catalog does not know it: `GetCandidatePaths` gives the final path and then the preliminary one, and `FindTableAsync` tries them in that order. For a year that is not in the final folder yet, such as the current one, it sends two more `SIZE` requests per table: when the listing of the final folder does not show the file, it asks the server for the file and for a first lettered part before it looks in the preliminary folder. The preliminary folder is optional. If the server answers `550` to its listing (FTP uses `550` both for a folder that does not exist and for one that may not be read), `ListTablesAsync` returns the tables of the final folder only, and `FindTableAsync`, when it finds no table, says in its `FileNotFoundException` that the preliminary folder could not be listed. A `550` to the listing of the final folder, and any other permanent reply to either listing, is a `DatasusRequestRefusedException`. For SIM and SINASC, Ordinance SVS No. 116 of 11 February 2009, art. 37 (`/SIM/CID10/DOCS/Legislacao_PDF.pdf`, p. 117), says that the data of a year are released as preliminary between 30 June and 30 August of the following year, and as official by 30 December of that year; it does not name the folders.
- **Letter case.** The server lists most names in upper case with a lower-case extension, but not all: in the listings of October 2026, `DORR2011.DBC`, `DNRR2010.DBC`, `AQPA1001.DBC` and `sppa1001.dbc` were listed like that. `GetPath` always builds the name in upper case with a lower-case extension; the listing has the real case, which is what `FindTableAsync` and `ListTablesAsync` return. In October 2026 the server ignored case: a file listed as `DORR2011.DBC` answered under `dorr2011.dbc` too.
- **Gaps.** In the listings of October 2026, SIASUS-PA, SIHSUS RD and SP, CNES LT, EQ, ST, PF, HB, DC and EP, SIM-DO and SINASC-DN had no missing period between the first and the last file of each state. The other file types missed some state-months; SIASUS-AR had no file of Roraima, and CNES-EF none of Roraima or the Federal District. Some file types had files of fewer states: SIASUS-AMP of 13, SIASUS AB and ABO of 18, CNES-EE of 23, SIASUS-SAD of 25 and CIHA of 26 (none of Roraima). The latest months also arrive state by state: for several file types, the last month listed was not there for every state.
- **Series that have ended.** In the listings of October 2026, the last files of SIASUS-AN were of 2014-10 and those of SIASUS-SAD of 2018-10. CNES-EE had, after 2018-12, files of 2019-12 and one file of 2021-07. SIASUS-AB had no file of 2016-11, of 2016-12, or from 2017-05 to 2024-12. Four sources had no file after a month long past: CIH-CR had files from 2008-01 to 2011-04, of 24 states, four months beyond the 2010-12 that the name of its folder suggests; SISPRENATAL-PN from 2012-01 to 2014-12; SISCOLO CC and HC from 2006-01 to 2015-10; and SISMAMA CM, HM and MM from 2009-01 to 2015-07. The catalog builds a path for any month from **From** on, whether the file is there or not, up to ninety-nine years after the year in which the folder layout in use now starts (2107 for SIASUS, SIHSUS and CIH, 2104 for CNES, 2110 for CIHA, 2111 for SISPRENATAL, 2105 for SISCOLO, 2108 for SISMAMA): the file name carries two digits of the year, and a later month would get the name of an earlier one, so it fails with `ArgumentOutOfRangeException`.
- **Files with no records.** Some files hold a header and no record at all, as two SIHSUS files of Acre in 2009-09 did in October 2026 (`SPAC0909.dbc`, `RJAC0909.dbc`). They open as an empty table.
- **Files that are not data.** The folders also hold a few other files, which are not part of any dataset: a placeholder named like `HBufAAmm.dbc` in five CNES folders, and two `.zip` files in the SIHSUS folder.
- **Size of the folders.** `/SIASUS/200801_/Dados` holds every SIASUS file type and listed 54,478 files; `/SIHSUS/200801_/Dados` listed 22,915; each CNES folder, up to 6,831; `/CIHA/201101_/Dados`, 4,798.

## Older layouts

Six file types were in other folders before the layout in use now, two of them with other file names. The catalog builds these too: a period from **From** to **To** gets the folder and the file name of this table, with no folder of preliminary data, and `ListTablesAsync` over a range that crosses both layouts lists the folder of each. Only the files of a state are built.

| File type | From | To | Folder | File name | Checked |
|---|---|---|---|---|---|
| SIASUS-PA | 1994-07 | 2007-12 | `/SIASUS/199407_200712/Dados` | `PA{UF}{YY}{MM}.dbc` | read |
| SIHSUS-RD | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `RD{UF}{YY}{MM}.dbc` | read |
| SIHSUS-SP | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `SP{UF}{YY}{MM}.dbc` | read |
| SIHSUS-RJ | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `RJ{UF}{YY}{MM}.dbc` | read |
| SIM-DO | 1979 | 1995 | `/SIM/CID9/DORES` | `DOR{UF}{YY}.dbc` | read |
| SINASC-DN | 1994 | 1995 | `/SINASC/1994_1995/Dados/DNRES` | `DNR{UF}{YYYY}.dbc` | read |

- **From** is the start of the folder, not the first period with data: in the listings of October 2026, SIHSUS-SP had files from 1997-06 and SIHSUS-RJ from 2006-04.
- **Checked** is as in the tables above: of each of the six, one file with records was read whole in October 2026. The first file tried of SIASUS-PA and of SIHSUS-SP (`PAMA0409.dbc`, `SPAP0710.dbc`) had a header and no record, and another was read. Files of one folder do not all have the same fields: the two SIASUS-PA files had 28 and 20 fields, and the two SIHSUS-SP files 18 and 16.
- **The hundred years of a two-digit year are counted in each layout.** `RDSP0712.dbc` is 2007-12 in the older folder; in the folder in use now the same name would be 2107-12.

The `R` of `DOR` stands for residence: the TAB user manual `/SIM/CID9/DOCS/MTAB16M.pdf` (p. 79; undated, it describes the data of 1979 to 1995) says that `DORuuaa.DBC` holds the deaths by state of residence, `uu` being the state and `aa` the year of the death.

## Outside the catalog

These are on the server and are reached with the path as a string. For most of them only the names were seen. Where a file was opened, the item says so: a header alone, or one small file read whole (decompressed, its CRC-32 checked, every field of every record).

- **Files for the whole country.** SIM and SINASC have one more file per year with `BR` in place of the state (`DOBR2011.dbc`, `DNBR2010.dbc`), and SINASC has `DNEX{YYYY}.dbc` from 2014. SIM also has yearly files for the whole country, with a two-digit year and no state in the name: `DOFET`, `DOEXT`, `DOINF` and `DOMAT` from 1996 and `DOREXT` from 2013 in `/SIM/CID10/DOFET`, the same five for 2025 and 2026 in `/SIM/PRELIM/DOFET` (those of 2025 were in both folders, with the same sizes), and `DOFET`, `DOEXT` and `DOINF` from 1979 to 1995 in `/SIM/CID9/DOFET`. SIHSUS has `CH` and `CM` files with `BR` only. Three headers were read: `DOMAT96.DBC` has the same fields (names, order, types and widths) as the SIM-DO file of Roraima for 1996, and `DNEX2020.dbc` the same as the SINASC-DN file of Roraima for 2020; `DOREXT20.dbc` has the fields of the SIM-DO file of Roraima for 2020 except one, and one that this file does not have.
- **SINAN.** Every file is for the whole country, with a two-digit year: `/SINAN/DADOS/FINAIS/DENGBR25.dbc`, and `/SINAN/DADOS/PRELIM` for preliminary years. The year in which a notifiable condition passes from preliminary to final differs from one condition to another. The file transfer page of the DATASUS website lists a file type `INFL`; the server had no file of it, and the form of that page returned none.
- **SIASUS-BI.** Since 2023-05, in MG, RJ and SP, its files have a `_1`, `_2` or `_3` suffix (`BIMG2305_1.dbc`), sometimes next to a file without a suffix. DbSus does not treat them as parts of one table.
- **SISCAN.** `/SISCAN/SISCAN` has `.csv` files for the whole country, by year, from 2013 to 2025, and no `.dbc`.
- **RESP and PCE.** Yearly and by state, with a two-digit year, which the catalog does not build: `/RESP/DADOS/RESP{UF}{YY}.dbc`, from 2015 to 2024, and `/PCE/Dados/PCE{UF}{YY}.dbc`, from 1995 to 2026, of 19 states. Both folders also have files with `BR` in place of the state. One small file of each was opened: that of PCE was read whole, and that of RESP has a header and no record.
- **PO and e-SUS Notifica.** For the whole country only: `/painel_oncologia/Dados/POBR{YYYY}.dbc`, from 2013 to 2026, and `DCCRBR{YY}.dbc`, of 2023 in `/ESUSNOTIFICA/DADOS/FINAIS` and of 2024 and 2025 in `/ESUSNOTIFICA/DADOS/PRELIM`. For `DCCR`, the note `/ESUSNOTIFICA/DOCS/Nota_Informativa_Doenca_de_Chagas_Cronica.pdf` (p. 1; undated, the file on the server is of 2026-07-31) says that the preliminary base is exported in January or February and in July of the following year, and is considered closed after two years.
- **IBGE.** `.zip` files, not `.dbc`: `/IBGE/POP/POPBR{YY}.zip`, `/IBGE/POPSVS/POPSBR{YY}.zip` and `/IBGE/POPTCU/POPTBR{YY}.zip`.
- **PNI.** `/PNI/DADOS` has files with the `.DBF` extension, not `.dbc`: `CPNI` and `DPNI`, by state and year, from 1994 to 2019. None was opened. The file transfer page does not list PNI.
- **Folders that look like leftovers.** `/SINASC/NOV` and `/SINASC/ANT` repeat names of the folders above, some with other sizes, and `/SIM/1996_` and `/SIM/1997_1995` are empty.
- **Documented but not on the server (October 2026).** Definition files and documents on the server name file types of which the listings showed no file: CMD `PR` and `CT`, the older SIASUS APAC files (`TA`, `OP`, `PC`, `PF`, `PQ`, `PR`), SIHSUS `MA`, SINAN `DENGON` and SIM `DOIuuaa`.

This is what the listings of October 2026 showed, not everything on the server: some folders were seen and not listed, and one refused its listing.

## Where the file transfer page and the server differ

The file transfer page of the DATASUS website states when some file types start or end. For these, the server had files outside those dates (observed in October 2026). The catalog follows the server, which is one more reason why it never checks availability.

| File type | The file transfer page says | The server has |
|---|---|---|
| SIASUS-PS | from January 2013 | files from 2012-11, for 6 states |
| SIASUS-ATD, SIASUS-ACF | from June 2014 | files from 2014-08 |
| CNES-GM | from June 2007 | files from 2007-03, for 16 states |
| CNES-IN | from November 2007 | files from 2007-10, for 10 states |
| SIASUS-AB | until March 2013 | files until 2025-07 |
| SIASUS-ABO | from April 2013 | files from 2014-01 |
| SIASUS-SAD | from November 2012 | files from 2012-04, for 5 states |
| SISCOLO CC and HC, SISMAMA CM and HM | 2006 to 2014 | SISCOLO files until 2015-10; SISMAMA files from 2009-01 to 2015-07 |
