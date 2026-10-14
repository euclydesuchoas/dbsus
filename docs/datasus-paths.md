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
- **Older folder layouts are built for nine file types.** For SIASUS-PA, SIHSUS RD, SP and RJ, SIM DO, DOEXT, DOFET and DOINF, and SINASC-DN, a period before the layout in use now gets the folder and the file name it had then (see "Older layouts" below). A period before the dataset's `FirstPeriod` fails with `ArgumentOutOfRangeException`.
- **The file for the whole country has methods of its own.** SIM-DO, SINASC-DN, PCE and RESP have, beside the files of the states, one file per year for the whole country: `GetNationalPath` builds its path, and `GetPath` never does. Another 69, the 58 of SINAN among them, have a file for the whole country and none by state, and are reached with the same methods (see "Files for the whole country" below).
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

In the file names, `{CODE}` is the code of the file type, `{UF}` is the two-letter abbreviation of the state, `{YY}` and `{YYYY}` are the year with two or four digits, and `{MM}` is the month with two digits. A yearly name with `{YY}` (PCE, RESP, ESUSNOTIFICA-DCCR, the five SIM file types for the whole country only and every one of SINAN) has the same limit of a hundred years as a monthly one (see "Series that have ended" below).

- **Official name** is the name under which DATASUS publishes the file type, in Portuguese and spelled as DATASUS spells it (`DatasusDataset.OfficialName`). It is `null` for SIHSUS CH, CM and MT, shown in the tables as "(none published)": the file transfer page of the DATASUS website does not list them, and the documents and definition files of the server that were searched do not name them.
- **From** is the first period the catalog accepts (`DatasusDataset.FirstPeriod`): the start of the oldest folder layout it knows for the file type (see "Older layouts" below), not the first period with data. CNES-LT, for example, has files from 2005-10 only, SIASUS-ABO from 2014-01 and SIASUS-AMP from 2016-03; of the file types for the whole country only, SIHSUS CH and CM have files from 2019-01, SIM-DOREXT from 2013 and SINASC-DNEX from 2014.

The tables below say, for each of the 112 file types, what was checked in October 2026: `read` means that one real file was read whole with DbSus (decompressed, its CRC-32 checked, every field of every record), usually a small and old one, and some held a single record; `header`, that a header alone was read, or that the file read had no record; `names`, that only the names were seen in a listing; and **no file**, that the listings showed no file of the file type, which is so of SINAN-INFL alone. From the `PRELIM` folders, two files were read in the same way in October 2026: SIM-DO and SINASC-DN of Roraima, both of 2026. That says nothing about other periods, or about other files of those folders. In twelve SINAN files, some fields did not convert (see SINAN below).

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
| SIASUS | BI | Produção Ambulatorial Individualizado | state | monthly | 2008-01 | read |

The name of SIASUS-AMP comes from a document in the SIASUS folder of the server; the file transfer page of the DATASUS website does not list this file type. The name of SIASUS-BI comes from the document `/SIASUS/200801_/Doc/Informe_Tecnico_SIASUS_2019_07.pdf`; that page does not list it either. A table of SIASUS-BI of a large state is in numbered parts (see "What to know about these files" below).

### SIHSUS

Folder `/SIHSUS/200801_/Dados`, file name `{CODE}{UF}{YY}{MM}.dbc`.

SIHSUS RD, SP and RJ are in this folder from 2008-01 on; for earlier months, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIHSUS | RD | AIH Reduzida | state | monthly | 1992-01 | read |
| SIHSUS | SP | Serviços Profissionais | state | monthly | 1992-01 | read |
| SIHSUS | RJ | AIH Rejeitadas | state | monthly | 1992-01 | read |
| SIHSUS | ER | AIH Rejeitadas com código de erro | state | monthly | 2008-01 | read |

### SIHSUS: CH and CM

Folder `/SIHSUS/200801_/Dados`, file name `{CODE}BR{YY}{MM}.dbc`.

One file per month for the whole country, in the folder of the files of the states, and none by state.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIHSUS | CH | (none published) | country | monthly | 2008-01 | read |
| SIHSUS | CM | (none published) | country | monthly | 2008-01 | read |

**From** is the start of the folder. In the listings of October 2026, each of the two had 42 files, from 2019-01 to 2026-03, and none for the other 45 months of that range. The files read whole in October 2026 were `CHBR2001.dbc` (3,357 records, 7 fields) and `CMBR2001.dbc` (1,048 records, 3 fields). SIHSUS-CM is not SISMAMA-CM: each of the two sources has a file type with the code `CM`, and a dataset is named by its source and its code together.

**What the two files show.** This is a description of ours, from the names of the fields, and not a name that DATASUS gives. `CHBR2001.dbc` has seven fields, all of type `C`: `CNES`, `RAZAO`, `NOMEFANT`, `CGC_HOSP`, `MUNIC_LOC`, `CNPJ_MANT` and `CMPT`. `CMBR2001.dbc` has three, of type `C` too: `CNPJ_MANT`, `RAZAO` and `COMPET`. By these names, a record of CH identifies a hospital (its CNES code, its names, its CNPJ, its municipality and the CNPJ of the entity that maintains it), and a record of CM identifies such an entity (its CNPJ and its name), each with a month. A definition file of the server, `RD2008.DEF` in `/SIHSUS/200801_/Auxiliar/TAB_SIH.zip`, tabulates SIHSUS-RD with two conversion tables that go by the same field names: "Hospital BR (CNPJ)", by `CGC_HOSP` and `NOMEFANT`, from a file `TCHBR.DBF`, and "Mantenedora", by `CNPJ_MANT` and `RAZAO`, from a file `TMANTBR.DBF`. No document that was read says what CH and CM are, and what the records hold was not examined: the two files were read to count them.

### SIHSUS: MT

Folder `/SIHSUS/Arquivos_MTBR`, file name `MTBR{YY}{MM}.dbc`.

One file per month for the whole country, in a folder of its own, and none by state.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIHSUS | MT | (none published) | country | monthly | 1995-01 | read |

The folder has no date in its name: **From** is the first month with files in the listings of October 2026, which showed 29 files, one for each month from 1995-01 to 1997-05. The code `MT` is read from the names of the files. The file read whole in October 2026 was the smallest of the folder, `MTBR9612.dbc` (37,259,445 bytes, 3,995,417 records, 17 fields).

**What the file shows.** This is a description of ours, from the names of the fields, and not a name that DATASUS gives. The 17 fields of `MTBR9612.dbc`, of types `C` and `N`, are `ANO_MES`, `ID_CGC_CPF`, `CGC_CPF`, `CGC_HOSP`, `N_AIH`, `RATEIO`, `ATO_PROF`, `TIPO_ATO`, `QTD_ATO`, `TIPO`, `SP_PONTO`, `VAL_ATO`, `ESPEC`, `UF_ZI`, `NUM_PROC`, `UF_DOM` and `UF_VINC`. By these names, a record is one act of a hospital admission (an AIH): the month, who performed the act (a CPF or a CNPJ), the hospital, the number of the AIH, the act, its quantity, its points and its value. No definition file and no document that was read names the file type; of its fields, only `UF_ZI` is in the definition files of 1992 to 1997, as a field of SIHSUS RD and RJ. What the records hold was not examined: the file was read to count them.

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

### SIM: files for the whole country only

From 1996 on: folder `/SIM/CID10/DOFET`, and `/SIM/PRELIM/DOFET` for preliminary years; file name `{CODE}{YY}.dbc`.

For 1979 to 1995, DOEXT, DOFET and DOINF are in an older folder: see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SIM | DOEXT | Declarações de Óbitos por causas externas | country | yearly | 1979 | read |
| SIM | DOFET | Declarações de Óbitos fetais | country | yearly | 1979 | read |
| SIM | DOINF | Declarações de Óbitos infantis | country | yearly | 1979 | read |
| SIM | DOMAT | Declarações de Óbitos maternos | country | yearly | 1996 | read |
| SIM | DOREXT | Mortalidade de residentes no exterior | country | yearly | 1996 | read |

These names have no state and two digits of the year. In the listings of October 2026, `/SIM/CID10/DOFET` had DOEXT, DOFET, DOINF and DOMAT from 1996 to 2025 and DOREXT from 2013 to 2025, and `/SIM/PRELIM/DOFET` had the five for 2025 and 2026; those of 2025 were in both folders, with the same sizes. **From** of DOREXT is the start of its folder. One file of each was read whole in October 2026: `DOEXT96.DBC` (119,156 records), `DOINF97.DBC` (71,400) and `DOMAT96.DBC` (1,520) have the same fields (names, order, types and widths) as the SIM-DO file of Roraima for 1996; `DOFET97.dbc` (41,584) has them too, one with another width; and `DOREXT20.dbc` (318) has the fields of the SIM-DO file of Roraima for 2020 except one, and one that this file does not have.

### SINASC

From 1996 on: folder `/SINASC/1996_/Dados/DNRES`, and `/SINASC/PRELIM/DNRES` for preliminary years; file name `DN{UF}{YYYY}.dbc`.

For 1994 and 1995, see "Older layouts" below.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SINASC | DN | Declarações de nascidos vivos | state and country | yearly | 1994 | read |

### SINASC: DNEX

Folder `/SINASC/1996_/Dados/DNRES`, and `/SINASC/PRELIM/DNRES` for preliminary years; file name `DNEX{YYYY}.dbc`.

One file per year for the whole country, in the folders of the files of the states, and none by state.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SINASC | DNEX | Declarações de nascidos vivos residentes no exterior | country | yearly | 1996 | read |

**From** is the start of the folder. In the listings of October 2026, DNEX had files from 2014 to 2024 in the final folder and for 2025 and 2026 in the preliminary one. The preliminary folder also had `DNEX25.dbc` and `DNEX26.dbc`, with two digits of the year and other sizes than `DNEX2025.dbc` and `DNEX2026.dbc`: they are not files of the pattern, and the catalog does not read them as tables. `DNEX2020.dbc` (507 records), read whole in October 2026, has the same fields (names, order, types and widths) as the SINASC-DN file of Roraima for 2020.

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

### PCE

Folder `/PCE/Dados`, file name `PCE{UF}{YY}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| PCE | PCE | Programa de Controle da Esquistossomose | state and country | yearly | 1995 | read |

### RESP

Folder `/RESP/DADOS`, file name `RESP{UF}{YY}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| RESP | RESP | Notificações de casos suspeitos de SCZ | state and country | yearly | 2015 | read |

The two folders have no date in their names: **From** is the first year with files in the listings of October 2026. In those listings, PCE had files of 19 states, from 1995 to 2026, and RESP of every state, from 2015 to 2024. Both folders also have a file for the whole country, with `BR` in place of the state (see "Files for the whole country" below). Of RESP, the first small file opened had a header and no record; another one, with a record, was then read whole.

### ESUSNOTIFICA

Folder `/ESUSNOTIFICA/DADOS/FINAIS`, and `/ESUSNOTIFICA/DADOS/PRELIM` for preliminary years; file name `DCCRBR{YY}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| ESUSNOTIFICA | DCCR | Doença de Chagas Crônica | country | yearly | 2023 | read |

One file per year for the whole country, and none by state. The folders have no date in their names: **From** is the first year with files in the listings of October 2026, in which the final folder had the file of 2023 and the preliminary one those of 2024 and 2025. `DCCRBR23.dbc` (5,455 records, 109 fields) was read whole in October 2026. The note `/ESUSNOTIFICA/DOCS/Nota_Informativa_Doenca_de_Chagas_Cronica.pdf` (p. 1; undated, the file on the server is of 2026-07-31) says that the preliminary base is exported in January or February and in July of the following year, and is considered closed after two years.

### PO

Folder `/painel_oncologia/Dados`, file name `POBR{YYYY}.dbc`.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| PO | PO | Painel de Oncologia | country | yearly | 2013 | read |

One file per year for the whole country, and none by state. The folder has no date in its name: **From** is the first year with files in the listings of October 2026, which showed one file for each year from 2013 to 2026. `POBR2013.dbc` (180,883 records, 23 fields) was read whole in October 2026.

### SINAN

Folder `/SINAN/DADOS/FINAIS`, and `/SINAN/DADOS/PRELIM` for preliminary years; file name `{CODE}BR{YY}.dbc`.

One file type per notifiable condition, each with one file per year for the whole country, and none by state. **One of them, INFL, had no file on the server in October 2026:** see "INFL has no known file", below the table.

| Source | Code | Official name | Coverage | Published | From | Checked |
|---|---|---|---|---|---|---|
| SINAN | ANIM | Acidente por Animais Peçonhentos | country | yearly | 2007 | read |
| SINAN | ANTR | Atendimento Antirrabico | country | yearly | 2006 | read |
| SINAN | AIDA | AIDS em adultos | country | yearly | 2007 | read |
| SINAN | AIDC | AIDS em crianças | country | yearly | 2007 | read |
| SINAN | BOTU | Botulismo | country | yearly | 2007 | read |
| SINAN | COLE | Cólera | country | yearly | 2007 | read |
| SINAN | COQU | Coqueluche | country | yearly | 2007 | read |
| SINAN | DENG | Dengue | country | yearly | 2000 | read |
| SINAN | DIFT | Difteria | country | yearly | 2007 | read |
| SINAN | DCRJ | Doença de Creutzfeldt-Jakob (DCJ) | country | yearly | 2007 | read |
| SINAN | CHAG | Doença de Chagas Aguda | country | yearly | 2000 | read |
| SINAN | EXAN | Doença exantemáticas | country | yearly | 2007 | read |
| SINAN | ESQU | Esquistossomose | country | yearly | 2007 | read |
| SINAN | ESPO | Esporotricose (Epizootia) | country | yearly | 2013 | read |
| SINAN | CHIK | Febre de Chikungunya | country | yearly | 2014 | read |
| SINAN | FMAC | Febre Maculosa | country | yearly | 2007 | read |
| SINAN | FTIF | Febre Tifóide | country | yearly | 2007 | read |
| SINAN | HANS | Hanseníase | country | yearly | 2001 | read |
| SINAN | HANT | Hantavirose | country | yearly | 1999 | read |
| SINAN | HEPA | Hepatites Virais | country | yearly | 2007 | read |
| SINAN | HIVA | HIV em adultos | country | yearly | 2007 | read |
| SINAN | HIVC | HIV em crianças | country | yearly | 2007 | read |
| SINAN | HIVE | HIV em crianças expostas | country | yearly | 2015 | read |
| SINAN | HIVG | HIV em gestante | country | yearly | 2007 | read |
| SINAN | INFL | Influenza Pandêmica | country | yearly | 1999 | **no file** |
| SINAN | IEXO | Intoxicação Exógena | country | yearly | 2006 | read |
| SINAN | LEIV | Leishmaniose Visceral | country | yearly | 2000 | read |
| SINAN | LTAN | Leishmaniose Tegumentar Americana | country | yearly | 2000 | read |
| SINAN | LEPT | Leptospirose | country | yearly | 2000 | read |
| SINAN | MALA | Malária | country | yearly | 2004 | read |
| SINAN | MENI | Meningite | country | yearly | 2007 | read |
| SINAN | PFAN | Paralisia Flácida Aguda | country | yearly | 2007 | read |
| SINAN | PEST | Peste | country | yearly | 2007 | read |
| SINAN | RAIV | Raiva | country | yearly | 2007 | read |
| SINAN | ROTA | Rotavírus | country | yearly | 2009 | read |
| SINAN | SIFA | Sífilis Adquirida | country | yearly | 2010 | read |
| SINAN | SIFC | Sífilis Congênita | country | yearly | 2007 | read |
| SINAN | SIFG | Sífilis em Gestante | country | yearly | 2007 | read |
| SINAN | SRC | Síndrome da Rubéola Congênia | country | yearly | 2007 | read |
| SINAN | SDTA | Surto Doenças Transmitidas por Alimentos | country | yearly | 2007 | read |
| SINAN | TETA | Tétano Acidental | country | yearly | 2007 | read |
| SINAN | TETN | Tétano Neonatal | country | yearly | 2014 | read |
| SINAN | TOXC | Toxoplasmose Congênita | country | yearly | 2019 | read |
| SINAN | TOXG | Toxoplasmose Gestacional | country | yearly | 2019 | read |
| SINAN | NTRA | Notificação de Tracoma | country | yearly | 2010 | read |
| SINAN | TRAC | Inquérito de Tracoma | country | yearly | 2009 | read |
| SINAN | TUBE | Tuberculose | country | yearly | 2001 | read |
| SINAN | VARC | Varicela | country | yearly | 2007 | read |
| SINAN | VIOL | Violência doméstica, sexual e/ou outras violências | country | yearly | 2009 | read |
| SINAN | ZIKA | Zika Vírus | country | yearly | 2015 | read |
| SINAN | ACBI | Acidente de trabalho com material biológico | country | yearly | 2006 | read |
| SINAN | ACGR | Acidente de trabalho | country | yearly | 2006 | read |
| SINAN | CANC | Cancêr relacionado ao trabalho | country | yearly | 2007 | read |
| SINAN | DERM | Dermatoses ocupacionais | country | yearly | 2006 | read |
| SINAN | LERD | LER/Dort | country | yearly | 2006 | read |
| SINAN | PAIR | Perda auditiva por ruído relacionado ao trabalho | country | yearly | 2006 | read |
| SINAN | PNEU | Pneumoconioses realacionadas ao trabalho | country | yearly | 2006 | read |
| SINAN | MENT | Transtornos mentais relacionados ao trabalho | country | yearly | 2006 | read |

- **INFL has no known file.** The file transfer page of the DATASUS website lists a SINAN file type `INFL`, *Influenza Pandêmica*, and the catalog has it for that reason, so that who knows that list finds it here. In October 2026 the server had no file of it in either folder, and the form of that page returned none for any year from 1999 to 2026. What the catalog has is a guess by the pattern of the other 57: the two folders above and the file name `INFLBR{YY}.dbc`. Of the name, only the stem was seen: a definition file of `/SINAN/AUXILIAR/TAB_SINANNET.zip`, titled "INVESTIGAÇÃO DE INFLUENZA - Sinan NET", tabulates files named `INFL*`. The folder, the rest of the name and the first year may turn out otherwise if DATASUS publishes the file. Until then `FindNationalTableAsync` and `OpenNationalReaderAsync` fail for it with a `FileNotFoundException` whose message says that no file of SINAN-INFL was known on the server in October 2026, and `ListNationalTablesAsync` returns no table.
- **From.** The two folders have no date in their names: **From** is the year of the first file of the file type in the listings of October 2026, in either folder. INFL had no file: its **From**, 1999, is the earliest one of the other SINAN file types (the one of HANT), so that no year the two folders hold is refused, and it says nothing of when a file of INFL would start.
- **Final and preliminary.** In those listings, of the 57 with files, 41 had them in both folders, three in the final one only (ESPO, LEIV and LTAN) and 13 in the preliminary one only; no year of a condition was in both folders. The year in which a condition passes from the preliminary folder to the final one differs from one condition to another (the final folder had SDTA up to 2018 and DENG up to 2025), so the catalog does not know it: `GetNationalCandidatePaths` gives the final path and then the preliminary one, and `FindNationalTableAsync` tries them in that order.
- **Conditions whose files are only in the preliminary folder.** In the listings of October 2026, thirteen SINAN file types had files in `/SINAN/DADOS/PRELIM` and none in `/SINAN/DADOS/FINAIS`: AIDA, AIDC, EXAN, HEPA, HIVA, HIVC, HIVE, HIVG, SIFA, SIFC, SIFG, SRC and VARC. `OpenNationalReaderAsync` opens final data only, so for them it fails with a `FileNotFoundException` that says that only the preliminary table exists. Preliminary data is read by choice: find the table, see that it is preliminary, and open it.

```csharp
using System;
using DbSus.Client;

await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });

DatasusTableInfo table = await client.FindNationalTableAsync(DatasusCatalog.Sinan.HIVA, DatasusPeriod.OfYear(2024));
Console.WriteLine($"{table.Path}, preliminary: {table.IsPreliminary}");   // /SINAN/DADOS/PRELIM/HIVABR24.dbc, preliminary: True
using var reader = await client.OpenReaderAsync(table);
```

- **Gaps and a stray name.** In the listings of October 2026, SDTA had no file of 2014, and COLE none of 2023 (its final files went up to 2022 and its preliminary ones were of 2024 and 2025). `/SINAN/DADOS/FINAIS` also had `LERBR19.dbc`, of the same size as `LERDBR19.dbc`; it is of no file type, and the catalog does not read it.
- **Checked.** In October 2026 one file with records was read whole of each of the 57 that had files, usually the smallest of the file type: 562,831 records in all, from 1 to 172,855 (`DENGBR00.dbc`). Of COLE, the first three files tried (`COLEBR07.dbc`, `COLEBR08.dbc` and `COLEBR09.dbc`) had a header and no record, and so had the first five of TETN (`TETNBR15.dbc`, `TETNBR18.dbc`, `TETNBR19.dbc`, `TETNBR21.dbc` and `TETNBR20.dbc`); the files read of the two are `COLEBR18.dbc` and `TETNBR14.dbc`, with one record each. Every file had fields of type `C`; all but `HANTBR99.dbc` had fields of type `D`, which no file read of another source had; and `SRCBR26.dbc` had one field of type `F`.
- **Fields that did not convert.** In twelve of the files read, of ANIM, AIDA, AIDC, HEPA, HIVA, HIVC, HIVE, HIVG, SIFA, SIFC, SIFG and SDTA, 68 fields of type `N` or `D` held, in some or all of the records, bytes that are not a number or a date. In 66 of them the field was filled with asterisks from end to end. In `SDTABR20.dbc`, the date field `DTTRANSSE` held groups of two digits separated by slashes in 12 records, and the numeric field `AGENTE_ET1` a letter in 2. By default the reader throws `FormatException` on such a field; `DbfReadOptions.UnconvertibleValues` reads it as null instead ("Values that do not convert", in [Reading DBC and DBF files](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md), says what each member of the reader does). The twelve files were read again with the option in October 2026, through the client. With `Null`, the twelve were read to the end with no exception, and 1,068,504 values of the 68 fields were null. With `AsterisksAsNull`, eleven were read to the end with no exception, with the values of the 66 fields null, and `SDTABR20.dbc` threw `FormatException` in its two other fields, in 14 records. The CRC-32 of every file matched. The table says, for each file, the fields and, in parentheses, the type of the field and the number of records in which it did not convert. This is of the one file read of each file type: it says nothing of other years, and whether the asterisks stand for a value that is missing is not written in any document that was read.

| File read | Records | Fields that did not convert |
|---|---|---|
| `ANIMBR07.dbc` | 103,757 | `ANO_NASC` (N, 5,923), `NU_AMPOLAS` (N, 82,951), `NU_AMPOL_1` (N, 99,410), `NU_AMPOL_8` (N, 98,987), `NU_AMPOL_6` (N, 99,716), `NU_AMPOL_4` (N, 100,730), `NU_AMPO_7` (N, 100,434), `NU_AMPO_5` (N, 99,952), `NU_AMPOL_9` (N, 93,637), `NU_AMPOL_3` (N, 100,748) |
| `AIDABR24.dbc` | 21,096 | `DT_NOTIFIC` (D, 10,622), `NU_IDADE_N` (N, 6), `ANTDTTRANS` (D, 21,059), `DTTRIAGEM` (D, 14,285), `DT_CONFIRM` (D, 13,611), `DT_RAPIDO` (D, 16,712), `DT_OBITO` (D, 19,836) |
| `AIDCBR24.dbc` | 104 | `DT_NOTIFIC` (D, 102), `IDADE_MAE` (N, 104), `ANTDTTRANS` (D, 104), `DT_TRIA_11` (D, 104), `DTCONFIRMA` (D, 104), `DTRAPIDO1` (D, 104), `DT_PCR_1` (D, 104), `DT_PCR_2` (D, 104), `DT_PCR_3` (D, 104), `EVO_DT_OBI` (D, 104) |
| `HEPABR20.dbc` | 21,597 | `DT_INVEST` (D, 27), `DT_ACIDENT` (D, 19,957), `DT_COLETA` (D, 18,830), `COLETAMARC` (D, 2,867), `DT_ENCERRA` (D, 68) |
| `HIVABR07.dbc` | 3,414 | `NU_IDADE_N` (N, 38), `ANTDTTRANS` (D, 3,368), `DTTRIAGEM` (D, 383), `DT_CONFIRM` (D, 414), `DT_RAPIDO` (D, 3,174), `DT_OBITO` (D, 3,231) |
| `HIVCBR24.dbc` | 1 | `IDADE_MAE` (N, 1), `ANTDTTRANS` (D, 1), `DT_TRIA_11` (D, 1), `DTCONFIRMA` (D, 1), `DTRAPIDO1` (D, 1), `DT_PCR_1` (D, 1), `DT_PCR_2` (D, 1), `DT_PCR_3` (D, 1), `EVO_DT_OBI` (D, 1) |
| `HIVEBR24.dbc` | 1,089 | `NU_IDADE_N` (N, 104), `DT_INVEST` (D, 1,089), `DT_OBITO` (D, 1,089), `DT_ENCERRA` (D, 1,089) |
| `HIVGBR24.dbc` | 3,598 | `PRE_DT_RET` (D, 1,650) |
| `SIFABR10.dbc` | 2,543 | `DT_INVEST` (D, 1,569), `DT_OBITO` (D, 2,542), `DT_ENCERRA` (D, 205) |
| `SIFCBR07.dbc` | 5,410 | `ANT_IDADE` (N, 142), `LAB_TITU_2` (N, 643), `LAB_DT3` (D, 456), `TRA_DT` (D, 2,459), `LABC_TIT_1` (N, 2,135), `LABC_DT_1` (D, 1,232), `LABC_DT` (D, 5,373), `LABC_TIT_2` (N, 5,249), `LABC_DT_2` (D, 4,046), `DT_OBITO` (D, 5,125) |
| `SIFGBR07.dbc` | 5,722 | `DTTESTE1` (D, 270) |
| `SDTABR20.dbc` | 292 | `DTTRANSSE` (D, 12), `AGENTE_ET1` (N, 2) |

- **Names.** The official names are those of the file transfer page of the DATASUS website. The definition files of `/SINAN/AUXILIAR/TAB_SINANNET.zip` and the documents of `/SINAN/DOCS/Docs_TAB_SINAN.zip`, as they were on the server in October 2026, give other names to four file types:
  - **NTRA and TRAC.** The website calls NTRA "Notificação de Tracoma" and TRAC "Inquérito de Tracoma". The definition file that tabulates `NTRA*` is titled "INQUÉRITOS DE TRACOMA", and the one that tabulates `TRAC*`, "Casos positivos de tracoma". Each file read has the fields of the definition of its own name: `NTRABR21.dbc` (66 records, 22 fields) has 8 of the 9 fields that the first one names, the counts `NU_CASOEXA` and `NU_CASOPOS` among them, and no field of sex or age; `TRACBR21.dbc` (63 records, 29 fields) has 15 of the 18 that the second one names, `CS_SEXO` and `NU_IDADE_N` among them. No document that was read says why the website and the definition files name the two differently.
  - **ACGR.** The website calls it "Acidente de trabalho"; the definition file that tabulates `Acgr*` is titled "INVESTIGAÇÃO DE ACIDENTE DE TRABALHO GRAVE", and `ACGRBR06.dbc` has 41 of the 44 fields that it names.
  - **ESPO.** The website calls it "Esporotricose (Epizootia)". No definition file tabulates `ESPO*`. The one of epizootics tabulates `EPIZ` and names 42 fields, of which `ESPOBR13.dbc` has 5; the data dictionary titled "Esporotricose (Epizootia)" names 16 of the 17 fields of that file.

## What to know about these files

- **Split tables.** In the listings of October 2026, SIASUS-PA was split into lettered parts (`PAMG2407a.dbc`, `PAMG2407b.dbc`, up to `d`) in five states: São Paulo in most months since 2011-12, Minas Gerais and Rio de Janeiro in every month since 2021-01, Rio Grande do Sul in some months since 2025-09, and Paraná from 2026-03 to 2026-05. `GetPath` gives the name without a part letter, `PAMG2407.dbc`; `FindTableAsync`, `FindPartsAsync` and `OpenReaderAsync` find the parts from it (see [downloading with DbSus.Client](https://github.com/euclydesuchoas/dbsus/blob/main/docs/client.md)).
- **Numbered parts (SIASUS-BI).** In the listings of October 2026, SIASUS-BI had 6,191 files of 6,021 tables (a state and a month), from 2008-01 to 2026-07. In Minas Gerais, Rio de Janeiro and São Paulo, in every month from 2023-05 on, the table was in numbered files (`BIMG2312_1.dbc`, `BIMG2312_2.dbc`): 257 files of 117 tables, two per table in 94 of them and three in 23, always numbered from 1 with none missing. Thirty of those tables, from 2023-05 to 2024-02, also had the file without a number (`BIMG2312.dbc`). Through the catalog (`FindTableAsync`, `ListTablesAsync` and `OpenReaderAsync` by dataset, state and period), the numbered files are the parts of one table, read in the order of their numbers, and the file without a number of the same state and month is left aside; a table that has only the file without a number is that file; a number missing before the last one is a `FileNotFoundException`. `GetPath` gives the name without a number. **This rule is a choice of the author of this library, not something DATASUS states.** No document that was read mentions the numbered files; the one that names the file type gives its file name as `BIufaamm.dbf`. The rule rests on what was seen: for Rio de Janeiro, 2023-06, the record counts in the headers of `_1` and `_2` add up to the one in the header of the file without a number, with the same fields; and in the thirty tables that had both, the numbered files were the newer ones. In those thirty tables, by the sizes and the modification times that the server gave in October 2026, the file without a number was of 2024-04-04 and the numbered files were from 72 to 337 days newer; the size of the file without a number, compressed, and the sum of the sizes of the numbered files were within 1% of each other in 11 tables and differed by more than 1% in 19 (the numbered files were the smaller in 13 and the larger in 6); and the headers of the file without a number and of its numbered files were read for four of them. In Rio de Janeiro of 2023-06 the record counts of the numbered files add up to the count of the file without a number, as said above. In the other three the numbered files have more records than it: 5,023,466 against 4,961,327 in Minas Gerais of 2023-12, 6,397,432 against 6,387,176 in Rio de Janeiro of 2023-12, and 7,524,778 against 7,271,039 in São Paulo of 2024-02; in the first two of these the numbered files are the smaller in compressed size. The twelve headers have the same 36 fields, the same record length and the same language driver ID. The records themselves were not compared, and no header was read of the other twenty-six tables. **No complete set of numbered parts was read:** the smallest one has 225 MB. What was read in October 2026: `BIAP0801.dbc`, a file without a number, whole (1,166 records, 35 fields); `BIMG2312_2.dbc`, one numbered part, whole (23,603 records, 36 fields); and the header alone of `BIMG2312_1.dbc` (4,999,863 records), which has the same 36 fields in the same order, the same record length and the same language driver ID as `BIMG2312_2.dbc`, so the reader accepts the two as parts of one table (checked with the two headers; the first part itself was not read). By path the rule does not apply: `FindPartsAsync`, `OpenReaderAsync` with a path and `DbSusFile.FindParts` know lettered parts only, and for them each of these names is a file of its own.
- **Preliminary years.** SIM and SINASC publish the most recent years in a folder of preliminary data until they are closed. In the listings of October 2026, the final folders went from 1996 to 2024 and the preliminary ones held 2025 and 2026. The SIM file types for the whole country only, SINASC-DNEX, ESUSNOTIFICA-DCCR and every one of SINAN have a folder of preliminary data too (see their tables above). Which years are preliminary changes, so the catalog does not know it: `GetCandidatePaths` gives the final path and then the preliminary one, and `FindTableAsync` tries them in that order. For a year that is not in the final folder yet, such as the current one, it sends two more `SIZE` requests per table: when the listing of the final folder does not show the file, it asks the server for the file and for a first lettered part before it looks in the preliminary folder. The preliminary folder is optional. If the server answers `550` to its listing (FTP uses `550` both for a folder that does not exist and for one that may not be read), `ListTablesAsync` returns the tables of the final folder only, and `FindTableAsync`, when it finds no table, says in its `FileNotFoundException` that the preliminary folder could not be listed. A `550` to the listing of the final folder, and any other permanent reply to either listing, is a `DatasusRequestRefusedException`. For SIM and SINASC, Ordinance SVS No. 116 of 11 February 2009, art. 37 (`/SIM/CID10/DOCS/Legislacao_PDF.pdf`, p. 117), says that the data of a year are released as preliminary between 30 June and 30 August of the following year, and as official by 30 December of that year; it does not name the folders. For SINAN, the manual of rules and routines of the system (2nd edition, 2007; `MANUAL/Manual_Normas e Rotinas_2ª_edicao.pdf` in `/SINAN/DOCS/Docs_TAB_SINAN.zip`), section 4.5.4, says that, for the conditions of the first list of that section, the database of a year is closed for statistics by 30 October of the following year; it names neither the folder `FINAIS` nor the folder `PRELIM`, and does not say that one of them holds the closed database.
- **Letter case.** The server lists most names in upper case with a lower-case extension, but not all: in the listings of October 2026, `DORR2011.DBC`, `DNRR2010.DBC`, `AQPA1001.DBC` and `sppa1001.dbc` were listed like that. `GetPath` always builds the name in upper case with a lower-case extension; the listing has the real case, which is what `FindTableAsync` and `ListTablesAsync` return. In October 2026 the server ignored case: a file listed as `DORR2011.DBC` answered under `dorr2011.dbc` too.
- **Gaps.** In the listings of October 2026, SIASUS-PA, SIHSUS RD and SP, CNES LT, EQ, ST, PF, HB, DC and EP, SIM-DO and SINASC-DN had no missing period between the first and the last file of each state. The other file types missed some state-months; SIASUS-AR had no file of Roraima, and CNES-EF none of Roraima or the Federal District. Some file types had files of fewer states: SIASUS-AMP of 13, SIASUS AB and ABO of 18, CNES-EE of 23, SIASUS-SAD of 25 and CIHA of 26 (none of Roraima). The latest months also arrive state by state: for several file types, the last month listed was not there for every state.
- **Series that have ended.** In the listings of October 2026, the last files of SIASUS-AN were of 2014-10 and those of SIASUS-SAD of 2018-10. CNES-EE had, after 2018-12, files of 2019-12 and one file of 2021-07. SIASUS-AB had no file of 2016-11, of 2016-12, or from 2017-05 to 2024-12. Four sources had no file after a month long past: CIH-CR had files from 2008-01 to 2011-04, of 24 states, four months beyond the 2010-12 that the name of its folder suggests; SISPRENATAL-PN from 2012-01 to 2014-12; SISCOLO CC and HC from 2006-01 to 2015-10; and SISMAMA CM, HM and MM from 2009-01 to 2015-07. SIHSUS-MT, for the whole country, had files from 1995-01 to 1997-05 only. The catalog builds a path for any month from **From** on, whether the file is there or not, up to ninety-nine years after the year in which the folder layout in use now starts (2107 for SIASUS, SIHSUS and CIH, 2094 for SIHSUS-MT, 2104 for CNES, 2110 for CIHA, 2111 for SISPRENATAL, 2105 for SISCOLO, 2108 for SISMAMA): the file name carries two digits of the year, and a later month would get the name of an earlier one, so it fails with `ArgumentOutOfRangeException`. The same holds for the yearly names with two digits of the year: PCE is built up to 2094, RESP up to 2114, the five SIM file types for the whole country only up to 2095, ESUSNOTIFICA-DCCR up to 2122, and each SINAN file type up to ninety-nine years after its **From** (2098 for HANT, 2118 for TOXC and TOXG).
- **Files with no records.** Some files hold a header and no record at all, as two SIHSUS files of Acre in 2009-09 did in October 2026 (`SPAC0909.dbc`, `RJAC0909.dbc`). They open as an empty table.
- **Files that are not data.** The folders also hold a few other files, which are not part of any dataset: a placeholder named like `HBufAAmm.dbc` in five CNES folders, and two `.zip` files in the SIHSUS folder.
- **Size of the folders.** `/SIASUS/200801_/Dados` holds every SIASUS file type and listed 54,478 files; `/SIHSUS/200801_/Dados` listed 22,915; each CNES folder, up to 6,831; `/CIHA/201101_/Dados`, 4,798.

## Older layouts

Nine file types were in other folders before the layout in use now, two of them with other file names. The catalog builds these too: a period from **From** to **To** gets the folder and the file name of this table, with no folder of preliminary data, and `ListTablesAsync` or `ListNationalTablesAsync` over a range that crosses both layouts lists the folder of each. The rows of SIM DOEXT, DOFET and DOINF are of files for the whole country, the only ones these file types have. The other rows are of the files of the states; the older folders of SIM-DO and SINASC-DN also have a file for the whole country (see "Files for the whole country" below).

| File type | From | To | Folder | File name | Checked |
|---|---|---|---|---|---|
| SIASUS-PA | 1994-07 | 2007-12 | `/SIASUS/199407_200712/Dados` | `PA{UF}{YY}{MM}.dbc` | read |
| SIHSUS-RD | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `RD{UF}{YY}{MM}.dbc` | read |
| SIHSUS-SP | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `SP{UF}{YY}{MM}.dbc` | read |
| SIHSUS-RJ | 1992-01 | 2007-12 | `/SIHSUS/199201_200712/Dados` | `RJ{UF}{YY}{MM}.dbc` | read |
| SIM-DO | 1979 | 1995 | `/SIM/CID9/DORES` | `DOR{UF}{YY}.dbc` | read |
| SIM-DOEXT | 1979 | 1995 | `/SIM/CID9/DOFET` | `DOEXT{YY}.dbc` | read |
| SIM-DOFET | 1979 | 1995 | `/SIM/CID9/DOFET` | `DOFET{YY}.dbc` | read |
| SIM-DOINF | 1979 | 1995 | `/SIM/CID9/DOFET` | `DOINF{YY}.dbc` | read |
| SINASC-DN | 1994 | 1995 | `/SINASC/1994_1995/Dados/DNRES` | `DNR{UF}{YYYY}.dbc` | read |

- **From** is the start of the folder, not the first period with data: in the listings of October 2026, SIHSUS-SP had files from 1997-06 and SIHSUS-RJ from 2006-04.
- **Checked** is as in the tables above: of each of the nine, one file with records was read whole in October 2026. The first file tried of SIASUS-PA and of SIHSUS-SP (`PAMA0409.dbc`, `SPAP0710.dbc`) had a header and no record, and another was read. Files of one folder do not all have the same fields: the two SIASUS-PA files had 28 and 20 fields, and the two SIHSUS-SP files 18 and 16. In the listings of October 2026, `/SIM/CID9/DOFET` had 17 files of each of DOEXT, DOFET and DOINF, from 1979 to 1995, all with the extension in upper case; the three read, `DOEXT79.DBC` (65,253 records), `DOFET90.DBC` (42,752) and `DOINF91.DBC` (86,969), have the same fields (names, order, types and widths) as the SIM-DO file of Roraima for 1979.
- **The hundred years of a two-digit year are counted in each layout.** `RDSP0712.dbc` is 2007-12 in the older folder; in the folder in use now the same name would be 2107-12.

The `R` of `DOR` stands for residence: the TAB user manual `/SIM/CID9/DOCS/MTAB16M.pdf` (p. 79; undated, it describes the data of 1979 to 1995) says that `DORuuaa.DBC` holds the deaths by state of residence, `uu` being the state and `aa` the year of the death.

## Files for the whole country

Four file types have, beside the files of the states, one file per year for the whole country, in the same folders. `GetNationalPath` and `GetNationalCandidatePaths` build its path, and `FindNationalTableAsync`, `ListNationalTablesAsync` and `OpenNationalReaderAsync` find, list and open it. Such a table has no state (`DatasusTableInfo.Uf` is `null`), and the methods by state never return it: leaving the state out never opens the file for the whole country.

Another 69 have a file for the whole country and none by state: the 58 of SINAN, and SIHSUS CH, CM and MT, SIM DOEXT, DOFET, DOINF, DOMAT and DOREXT, SINASC-DNEX, ESUSNOTIFICA-DCCR and PO. The same methods build, find, list and open their files; their folders and file names are in the tables of "The file types in the catalog" above. The table below is of the four that also have files by state.

| File type | Periods | Folder | File name | Checked |
|---|---|---|---|---|
| SIM-DO | 1979 to 1995 | `/SIM/CID9/DORES` | `DORBR{YY}.dbc` | header |
| SIM-DO | from 1996 | `/SIM/CID10/DORES`, and `/SIM/PRELIM/DORES` for preliminary years | `DOBR{YYYY}.dbc` | header |
| SINASC-DN | 1994 to 1995 | `/SINASC/1994_1995/Dados/DNRES` | `DNBR{YYYY}.dbc` | header |
| SINASC-DN | from 1996 | `/SINASC/1996_/Dados/DNRES`, and `/SINASC/PRELIM/DNRES` for preliminary years | `DNBR{YYYY}.dbc` | header |
| PCE | from 1995 | `/PCE/Dados` | `PCEBR{YY}.dbc` | read |
| RESP | from 2015 | `/RESP/DADOS` | `RESPBR{YY}.dbc` | read |

```csharp
using System;
using DbSus.Client;

Console.WriteLine(DatasusCatalog.Sim.DO.GetNationalPath(DatasusPeriod.OfYear(2011)));   // /SIM/CID10/DORES/DOBR2011.dbc
Console.WriteLine(DatasusCatalog.Sim.DO.GetNationalPath(DatasusPeriod.OfYear(1995)));   // /SIM/CID9/DORES/DORBR95.dbc
Console.WriteLine(DatasusCatalog.Pce.PCE.GetNationalPath(DatasusPeriod.OfYear(1999)));  // /PCE/Dados/PCEBR99.dbc
```

```csharp
using System;
using DbSus.Client;

await using var client = new DatasusClient(new DatasusClientOptions { CacheDirectory = "datasus-cache" });

// Final or preliminary: ask the server where the table is, then decide.
DatasusTableInfo table = await client.FindNationalTableAsync(DatasusCatalog.Sinasc.DN, DatasusPeriod.OfYear(2025));
Console.WriteLine($"{table.Path}: {table.Size} bytes, preliminary: {table.IsPreliminary}, state: {(table.Uf is null ? "none" : table.Uf.ToString())}");

// Final data only.
using (var reader = await client.OpenNationalReaderAsync(DatasusCatalog.Pce.PCE, DatasusPeriod.OfYear(1995)))
{
    while (reader.Read()) { }
}

// What the folders list, one entry per year.
var years = await client.ListNationalTablesAsync(DatasusCatalog.Resp.RESP, DatasusPeriod.OfYear(2015), DatasusPeriod.OfYear(2024));
```

- **A dataset without the file asked for says so.** `GetNationalPath` of a file type that has files by state only (SIHSUS-RD, for one) fails with `NotSupportedException`, and the client methods with `ArgumentException`; the message names the methods to use instead. It is the same the other way round: `GetPath` and the client methods by state fail in those ways for a file type that has a file for the whole country only (SIM-DOFET, for one).
- **The files are large.** In the listings of October 2026, `DOBR2026.dbc` had 73,395,328 bytes and `DNBR2026.dbc` 73,590,646. `OpenNationalReaderAsync` downloads the whole file before the first record is read.
- **What was listed in October 2026.** `DORBR` from 1979 to 1995; `DOBR` and `DNBR` from 1996 to 2024 in the final folders and for 2025 and 2026 in the preliminary ones; `DNBR` for 1994 and 1995 in the older folder; `PCEBR` from 1995 to 2026; and `RESPBR` from 2015 to 2024. None of these names had a part letter. `/RESP/DADOS` also had one `RESPBR.dbc`, with no year in its name, which is not a file of the pattern.
- **Checked** is as in the tables above. Of PCE and RESP, one file was read whole in October 2026: `PCEBR95.dbc` (85 records) has the same fields as the file of a state that was read, `PCEPE99.dbc`; `RESPBR24.dbc` (539 records) has 80 fields, two more than the files of a state that were read (`RESPAC21.dbc` and `RESPAC18.dbc`, with 78), and some of the fields they share have other widths.
- **Of SIM-DO and SINASC-DN only headers were read**, of six files, and each was compared with the header of a file of Roraima. Four have the same fields as that file (names, order, types and widths): `DOBR1996.dbc` (908,883 records) as the SIM-DO file of 1996, `DOBR2026.dbc` (912,010 records, in the preliminary folder) as the one of 2020, `DORBR79.dbc` (711,742 records) as the one of 1979, and `DNBR2026.dbc` (1,504,579 records, in the preliminary folder) as the SINASC-DN file of 2020. `DNBR1994.dbc` (2,571,571 records) has the 30 fields of the SINASC-DN file of 1994 with the same types and widths, 12 of them under other names. `DNBR1996.dbc` (2,945,425 records) has 21 fields and was not compared with a file of its year: of the files of a state, the nearest one whose fields were kept is of 2020 and has 61.
- **Whether the file for the whole country holds the records of the files of the states was not checked.**

## Outside the catalog

These are on the server and are reached with the path as a string. For most of them only the names were seen. Where a file was opened, the item says so: a header alone, or one small file read whole (decompressed, its CRC-32 checked, every field of every record).

- **SISCAN.** `/SISCAN/SISCAN` has `.csv` files for the whole country, by year, from 2013 to 2025, and no `.dbc`.
- **IBGE.** `.zip` files, not `.dbc`: `/IBGE/POP/POPBR{YY}.zip`, `/IBGE/POPSVS/POPSBR{YY}.zip` and `/IBGE/POPTCU/POPTBR{YY}.zip`.
- **PNI.** `/PNI/DADOS` has files with the `.DBF` extension, not `.dbc`: `CPNI` and `DPNI`, by state and year, from 1994 to 2019. None was opened. The file transfer page does not list PNI.
- **Folders that repeat names of other folders.** In the listings of October 2026, five folders held `.dbc` files under names that a folder of the catalog also has. They stay outside the catalog: no dataset reads them. No file of these folders was opened, so nothing is known of what they hold, nor of why some sizes differ.
  - `/SINASC/NOV/DNRES`: 734 files: 729 named `DN{UF}{YYYY}.dbc` (the 27 states, 1996 to 2022), the four `DNBR{YYYY}.dbc` of 2014 to 2017, and `DNEX2021.dbc`. Every one of these names is in `/SINASC/1996_/Dados/DNRES`, where SINASC-DN and SINASC-DNEX are read; 58 of the 734 have another size there (the 27 states of 2013, the 27 of 2016, and the four `DNBR`).
  - `/SINASC/ANT/DNRES`: 109 files: 54 named `DNR{UF}{YYYY}.dbc` and 54 named `DNR{UF}{YY}.DBC` (the 27 states, 1994 and 1995), and `DNAC2014.dbc`. The 54 names with four digits are in `/SINASC/1994_1995/Dados/DNRES`, the older layout of SINASC-DN, every one with another size there; the 54 with two digits have the size of the file of the same state and year of that folder; `DNAC2014.dbc` is in `/SINASC/1996_/Dados/DNRES` with another size. That is 55 of the 109 with another size. `/SINASC/ANT/DNIGN` has two more files (see the next item).
  - `/SIHSUS/MHJ_14_16`: 972 files named `RD{UF}{YY}{MM}.dbc`, the 27 states, 2014-01 to 2016-12. Every one of these names is in `/SIHSUS/200801_/Dados`, where SIHSUS-RD is read; 216 of the 972 have another size there (the 27 states, 2016-05 to 2016-12).
  - `/SIASUS/teste`: 11 files named `PA{UF}{YY}{MM}.dbc`, all of Paraná, `PAPR2408.dbc` to `PAPR2506.dbc`. Every one of these names is in `/SIASUS/200801_/Dados`, where SIASUS-PA is read; 10 of the 11 have another size there.
  - `/CIH/200801_201012/dados2`: one file, `CRAC0801.dbc`, of 1,325 bytes. The name is in `/CIH/200801_201012/Dados`, where CIH-CR is read, and the file there has the same size.

  In October 2026 the form of the file transfer page of the DATASUS website was queried eight times, seven on 2026-10-10 and one on 2026-10-11, and returned 40 files, none of them from these five folders. One of those queries asked for files that `/SINASC/NOV/DNRES` and `/SINASC/ANT/DNRES` also hold (SINASC-DN of Roraima, of 2010 and of 1995), and the page returned the ones of the folders of the catalog. None of the eight asked for a state and a period that the other three folders hold.
- **Files with `IG` in the place of the state.** `DORIG95.DBC`, of 8,953 bytes, is the only file of `/SIM/CID9/DOIGN`, and `DNRIG1995.dbc`, of 18,160 bytes, the only one of `/SINASC/1994_1995/Dados/DNIGN`. `/SINASC/ANT/DNIGN` has `DNRIG1995.dbc` and `DNRIG95.DBC`, of 18,160 bytes each. The four stay outside the catalog because such a name is neither the name of a file of a state (`IG` is not the abbreviation of one) nor the name of a file for the whole country. None was opened.
- **Empty folders.** `/SIM/1996_` and `/SIM/1997_1995` are empty.
- **Documented but not on the server (October 2026).** Definition files and documents on the server name file types of which the listings showed no file: CMD `PR` and `CT`, the older SIASUS APAC files (`TA`, `OP`, `PC`, `PF`, `PQ`, `PR`), SIHSUS `MA`, SINAN `DENGON`, SIM `DOIuuaa` and six file types that definition files of `/SINAN/AUXILIAR/TAB_SINANNET.zip` tabulate: yellow fever (`FAma*`), West Nile fever (`NILO*`), epizootics (`EPIZ`), outbreak notification (`NSurt*`), outbreak worksheet (`PSurt*`) and negative notification (`NNEGA*`). A seventh that they tabulate, influenza (`INFL*`), is in the catalog as SINAN-INFL, with no known file (see SINAN, above).

This is what the listings of October 2026 showed, not everything on the server: some folders were seen and not listed, and one refused its listing. "Coverage", below, has the count.

## Coverage

What the catalog and this guide were checked against, with the dates, and what they were not checked against. Here, that the catalog reads a file means that one of its datasets finds and opens the file by its folder and its name; it is not a check of what the file holds.

- **The server.** The listings were made on 2026-10-10, 2026-10-11 and 2026-10-12, one of each of 246 folders, from `/dissemin/publicos` down. They showed 195,263 `.dbc` files (the extension in either case), in 47 of those folders. The catalog reads 193,423 of them, each through one dataset and none through two, and every one of its datasets reads at least one, but SINAN-INFL, of which the listings had no file. Thirty of the files counted as read are the files without a number of SIASUS-BI that the client leaves aside when the table has numbered parts (see "Numbered parts", above). The other 1,840 files are read by no dataset. They are in eleven groups, every one named in this guide: the five folders that repeat names of other folders (1,827 files; "Outside the catalog"); the four files with `IG` in the place of the state, two of them in `/SINASC/ANT/DNIGN` (same section); the five placeholders of the CNES folders ("What to know about these files"); `LERBR19.dbc` (SINAN); `RESPBR.dbc` ("Files for the whole country"); and `DNEX25.dbc` and `DNEX26.dbc` (SINASC: DNEX). On 2026-10-14, before the release, the root of `/dissemin/publicos` and the 47 folders with `.dbc` files were listed again: the root had the same 25 folders, and the 47 held 195,267 `.dbc` files, four more than in October, all of them new files of CIHA; 193,427 are read by one dataset and none by two, and the same 1,840 are read by none, in the same eleven groups.
- **The file transfer page.** The list of file types that the page of the DATASUS website offers as data, as it was on 2026-10-11, has 109 distinct entries, a source and a code each. Of them, 106 are in the catalog under the same source and code: 105 of which the server had files, and SINAN `INFL`, of which it had none. The other three are IBGE `POP`, `POPS` and `POPT`, which are `.zip` files and stay outside. Six file types of the catalog are not in that list: SIASUS AMP and BI, SIHSUS CH, CM and MT, and SISMAMA-MM. The 106 and the six are the 112 of the catalog: of 111 of them the server had files, and of one, SINAN-INFL, it had none. The page offers other kinds of files too (documentation, auxiliary files for tabulation, programs, territorial bases, maps and conversions). What it offers under them was not read, and the catalog does not cover them; of the 246 folders listed, the 199 without a `.dbc` file include every folder of documentation, of auxiliary files and of programs.
- **Not checked.** `/uploads`, which refused its listing; 121 folders named `csv`, `json`, `xml` and `parquet` under `/Dados_Abertos/SINAN`, which were seen and not listed; what the form of the file transfer page returns for each file type, year and state (it was queried eight times); the other kinds of files of that page; the server above `/dissemin/publicos`; and whatever was published after 2026-10-12.

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
