# Performance

Every figure in this guide comes from one machine: AMD Ryzen 9 9950X3D, Windows 11, Release builds. They were measured with .NET SDK 10.0.402 (.NET 10.0.13, .NET 9.0.21, .NET 8.0.32 and .NET Framework 4.8.1), except the real-world test of the throughput section, which says how it was run. Other files, machines and runtimes will differ.

## Throughput

Measured with BenchmarkDotNet (`benchmarks/DbSus.Benchmarks`, .NET 10) on one large SIA-PA file: 126 MB compressed, 1.17 GB of DBF, 2,956,469 records of 60 fields.

To run it, from the repository root: `dotnet run -c Release --project benchmarks/DbSus.Benchmarks -- --filter "*"`. It takes no path: it reads `tests/data/local/PAMG2407b.dbc` (SIA-PA, Minas Gerais, July 2024, part b), found from the repository root. That file is not in the repository; it has to be downloaded from the DATASUS server, which republishes files, so another download can be a different version and give other numbers.

| What | DBF throughput | Allocated per record |
|---|---|---|
| Decompression only | 847 MB/s | 0.05 B |
| Decompression and reading the records | 730 MB/s | 0.06 B |
| Plus 3 typed getters per record | 661 MB/s | 0.06 B |
| Plus 2 converted character fields | 637 MB/s | 0.06 B |
| Every value of every field (`GetValues`, boxed) | 295 MB/s | 1,867 B |

The small allocations of the first four rows are fixed buffers, not per-record costs. Decompression dominates: it consumed the compressed file at about 78 MB/s (about 630 Mbit/s), 126 MB in about 1.6 s, the time the table gives for 1.17 GB at 730 MB/s. These figures are of files already on disk. When a file comes from the DATASUS server, `DbSus.Client` downloads it, or every part of the table, to a file before the first record is read, so the time of the network and the time of decompression add up instead of overlapping. With `VerifyOnDownload` (the default), a `.dbc` is also decompressed twice: once when its download ends, to check its CRC-32, and again when it is read; a file already in the cache is not downloaded again and is decompressed only when read. No figure in this guide includes a download.

In a real-world test with version 0.1.0-preview.1, before the CRC-32 and decoder changes below, on the same machine (Windows 11, .NET SDK 10.0.401, running on .NET 10), a complete SIA-PA month split into two parts, about 8 million records, was read from local files in about 11 seconds, with about 0 bytes allocated per record and no garbage collection. The files were probably already in the operating system's file cache; a cold read was not measured. The memory peak was 34 MiB: the peak working set (`PeakWorkingSet64`) of the whole process, the .NET runtime included.

### CRC-32

Every decompressed byte goes through the CRC-32 that the `.dbc` stores. After 0.1.0-preview.1, it is computed eight bytes at a time (slicing-by-8) instead of one. Measured on a complete SIA-PA month in two parts (1.70 GB and 1.37 GB of DBF), five runs of each, the first discarded; each range covers the two parts, and .NET Framework ran the `net472` build:

| What | .NET 10, before | .NET 10, after | .NET Framework 4.8.1, before | .NET Framework 4.8.1, after |
|---|---|---|---|---|
| CRC-32 alone, over the DBF already in memory | 693 to 703 MB/s | 2,918 to 2,971 MB/s | 625 to 631 MB/s | 2,087 to 2,131 MB/s |
| Full decompression (`DbcFile.OpenAsDbf`, read to the end) | 303 to 305 MB/s | 413 to 414 MB/s | 223 to 225 MB/s | 293 to 296 MB/s |

A full decompression took about 26% less time on .NET 10 and about 24% less on .NET Framework 4.8.1.

With `DbcReadOptions.VerifyChecksum` set to `false`, the CRC-32 is not computed at all, and a file whose data does not match its checksum is read without any error. Measured with the decoder of the next section, on the same two parts, in two sessions of five runs of each setting, the first discarded and the order of the settings reversed in the second session, a full decompression then took about 29% to 31% less time than with the check on .NET 10 and on .NET 8 (`netstandard2.0` build), and about 22% to 25% less on .NET Framework 4.8.1; the output was the same, byte for byte. Those sessions used another program, so their absolute times are not comparable with the table above.

### Decoder

After 0.1.0-preview.1, the decoder also takes Huffman codes through lookup tables instead of one bit at a time, and writes copies as blocks straight into the caller's buffer instead of one byte at a time through its window. Measured on the same two parts, a full decompression (`DbcFile.OpenAsDbf`, read to the end in 80 KiB reads, the CRC-32 checked), in two sessions of five runs of each version on each runtime, the order of the versions reversed in the second session and the first run of each discarded; each range covers the two parts and the two sessions, .NET 8 ran the `netstandard2.0` build and .NET Framework the `net472` build:

| Runtime | Before | Lookup tables | Lookup tables and block copies |
|---|---|---|---|
| .NET 10 | 421 to 445 MB/s | 636 to 664 MB/s | 833 to 862 MB/s |
| .NET 8 | 421 to 451 MB/s | 588 to 649 MB/s | 839 to 872 MB/s |
| .NET Framework 4.8.1 | 282 to 294 MB/s | 397 to 412 MB/s | 449 to 461 MB/s |

A full decompression took about 48% to 50% less time than before on .NET 10 and .NET 8, and about 35% to 39% less on .NET Framework 4.8.1, where the block copies gained least: 8% to 14% of the time with the lookup tables alone, against 21% to 30% on .NET 10 and .NET 8. Allocation did not change (154,288 bytes per file opened on .NET 10 and .NET 8, 8 bytes more than before; on .NET Framework, whose counter advances by blocks, 147,552 to 159,648 bytes before and after), and no garbage collection ran. These sessions used yet another program, so their absolute times are not comparable with the CRC-32 table or the `VerifyChecksum` figures either.

### Size of the reads

The throughput of `DbcFile.OpenAsDbf` depends on how much each `Read` asks for. Measured on the larger part (1.37 GB of DBF), the CRC-32 checked, with reads of one fixed size to the end of the data (reads of 1 and 16 bytes over the first 8 MiB only); each run is a new process that first reads the first MiB the same way, and the ranges are those of three runs, the two versions alternating. "Before" is the code from before the decoder changes above:

| Read size | .NET 10, before | .NET 10, after | .NET Framework 4.8.1, before | .NET Framework 4.8.1, after |
|---|---|---|---|---|
| 1 byte | 43 to 44 MB/s | 44 to 46 MB/s | 51 to 52 MB/s | 56 to 57 MB/s |
| 16 bytes | 57 to 59 MB/s | 144 to 149 MB/s | 227 to 233 MB/s | 247 to 249 MB/s |
| 397 bytes | 366 to 415 MB/s | 694 to 702 MB/s | 300 to 301 MB/s | 461 to 463 MB/s |
| 4 KiB | 414 to 425 MB/s | 720 to 723 MB/s | 302 to 310 MB/s | 460 to 474 MB/s |
| 80 KiB | 382 to 423 MB/s | 820 to 826 MB/s | 307 to 309 MB/s | 493 to 501 MB/s |
| 1 MiB | 399 to 432 MB/s | 828 to 843 MB/s | 308 to 310 MB/s | 497 to 504 MB/s |

Each read has a fixed cost, which dominates below a few hundred bytes. Read in blocks of a few tens of KiB or more: from 80 KiB, the throughput was within about 2% of that of 1 MiB reads, while 4 KiB reads gave about 86% of it on .NET 10 and about 93% on .NET Framework. Measured the same way, version 0.1.0-preview.1 read 1 byte at a time at the same speed as this version (45 MB/s on .NET 10, 56 MB/s on .NET Framework) and was slower at every other size.

## Reading only some fields

One real file, `RDMG2407.dbc` (an SIH-RD month: 132,337 records of 113 fields), read to the end as a `.dbc` on .NET 10. The projection keeps the first three character (`C`) fields in the order of the header. Each run is a new process, which opens the file's header once before the timed read; times are the range of three runs. The same program read the same file with this version and with the code from before the decoder changes above (after the CRC-32 changes), the two alternating:

| Reading | Fields | Time, before the decoder changes | Time | Allocated | Peak working set |
|---|---|---|---|---|---|
| `GetValues` | all 113 | 0.58 to 0.59 s | 0.48 to 0.53 s | 377 MiB (2,984 B per record) | 77 MiB |
| `GetValues` | 3 text fields | 0.38 to 0.39 s | 0.27 to 0.28 s | 13.3 MiB (106 B per record) | 42 MiB |
| Typed getters chosen by `GetFieldType`, text as raw bytes | all 113 | 0.50 to 0.55 s | 0.41 to 0.45 s | 215 KiB (1.7 B per record) | 32 MiB |
| The same | 3 text fields | 0.37 s | 0.26 s | 214 KiB | 32 MiB |
| Typed getters, text decoded with `GetChars` into a reused buffer | all 113 | 0.58 s | 0.48 to 0.49 s | 218 KiB | 32 MiB |
| The same | 3 text fields | 0.37 to 0.38 s | 0.26 to 0.27 s | 217 KiB | 31 MiB |

The allocation is that of this version; the earlier code allocated 17,280 bytes less in every row, which this version allocates once per process, at the first read of a `.dbc`, for the decoder's lookup tables. On this file and this machine:

- When values are materialized as objects (`GetValues`, `DataTable`), a projection cut allocation from 377 MiB to about 13 MiB and the time by 43% to 49%.
- With typed getters and text decoded through `GetChars`, allocation was already close to zero and a projection still saved 43% to 47% of the time, since decoding text costs time even without allocating.
- With text left as raw bytes, it saved 37% to 42%: most of what remains is decompression, which a projection does not reduce. For comparison, the same three fields of the same file already decompressed (`.dbf`), read through `GetValues`, took 55 to 58 ms; that `.dbf` had just been written, so it was probably in the operating system's file cache.
- The decoder changes took about 0.09 to 0.11 s off every row.

## Allocation of the reading loop

The loop in [reading without allocation](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md) was measured over the same SIH-RD file (132,337 records of 113 fields), as the `.dbc` and as the `.dbf` it decompresses to, in one process per runtime and file: a warm-up pass, then three measured passes, each leaving out what opening the reader allocates. The three passes gave the same figures, and so did the code from before the decoder changes, measured the same way:

| Runtime | Counter | `.dbf` | `.dbc` | Garbage collections |
|---|---|---|---|---|
| .NET 10 | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET 9 (`netstandard2.0` build) | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET 8 (`netstandard2.0` build) | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET Framework 4.8.1 (`net472` build) | `AppDomain.MonitoringTotalAllocatedMemorySize` | 0 bytes | 8,192 bytes | none |

The 4,120 bytes are a single 4 KB buffer at the end of the data, used to read what remains and verify the checksum. The .NET Framework counter advances by allocation blocks rather than per object, so it shows that buffer as one block of 8 KiB. On .NET 8, the loop allocates nothing, but the nullable forms of `GetFieldValue<T>` would: see [reading without allocation](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).
