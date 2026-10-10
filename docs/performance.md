# Performance

Every figure in this guide comes from one machine: AMD Ryzen 9 9950X3D, Windows 11, Release builds, .NET SDK 10.0.401. Other files, machines and runtimes will differ.

## Throughput

Measured with BenchmarkDotNet (`benchmarks/DbSus.Benchmarks`, .NET 10) on one large SIA-PA file: 126 MB compressed, 1.17 GB of DBF, 2,956,469 records of 60 fields.

To run it, from the repository root: `dotnet run -c Release --project benchmarks/DbSus.Benchmarks -- --filter "*"`. It takes no path: it reads `tests/data/local/PAMG2407b.dbc` (SIA-PA, Minas Gerais, July 2024, part b), found from the repository root. That file is not in the repository; it has to be downloaded from the DATASUS server, which republishes files, so another download can be a different version and give other numbers.

| What | DBF throughput | Allocated per record |
|---|---|---|
| Decompression only | 311 MB/s | 0.05 B |
| Decompression and reading the records | 309 MB/s | 0.06 B |
| Plus 3 typed getters per record | 299 MB/s | 0.06 B |
| Plus 2 converted character fields | 293 MB/s | 0.06 B |
| Every value of every field (`GetValues`, boxed) | 189 MB/s | 1,867 B |

The small allocations of the first four rows are fixed buffers, not per-record costs. Decompression dominates: it consumed the compressed file at about 33 MB/s (about 265 Mbit/s), 126 MB in about 3.8 s, the time the table gives for 1.17 GB at 309 MB/s. A connection slower than that is the limit when files come from the DATASUS server; on a faster one, decompression is.

In a real-world test on the same machine (Windows 11, .NET SDK 10.0.401, running on .NET 10), a complete SIA-PA month split into two parts, about 8 million records, was read from local files in about 11 seconds, with about 0 bytes allocated per record and no garbage collection. The files were probably already in the operating system's file cache; a cold read was not measured. The memory peak was 34 MiB: the peak working set (`PeakWorkingSet64`) of the whole process, the .NET runtime included.

## Reading only some fields

One file (an SIH-RD month: 132,337 records of 113 fields), read as a `.dbc`, each run in a new process; times are the range of two or three runs.

| Reading | Fields | Time | Allocated | Peak working set |
|---|---|---|---|---|
| `GetValues` | all 113 | 0.68 to 0.69 s | 377 MiB (2,984 B per record) | 77 MiB |
| `GetValues` | 3 projected | 0.48 to 0.50 s | 14.3 MiB (114 B per record) | 42 MiB |
| Typed getters chosen by `GetFieldType`, text as raw bytes | all 113 | 0.57 to 0.58 s | 201 KiB (1.6 B per record) | 28.5 MiB |
| The same | 3 projected | 0.47 to 0.49 s | 201 KiB | 28 MiB |
| Typed getters, text decoded with `GetChars` into a reused buffer | all 113 | 0.68 s | 205 KiB | not measured |
| The same | 3 projected | 0.48 s | 205 KiB | not measured |

On this file and this machine:

- When values are materialized as objects (`GetValues`, `DataTable`), a projection cut allocation from 377 MiB to about 14 MiB and the time by about 30%.
- With typed getters and text decoded through `GetChars`, allocation was already close to zero and a projection still saved about 30% of the time, since decoding text costs time even without allocating.
- With text left as raw bytes, it saved less (0.57 s to 0.47 s): most of the remaining time is decompression, which a projection does not reduce.
- For comparison, the same file already decompressed (`.dbf`), with 3 fields read through `GetValues`, took 173 ms on the first run and 63 ms on the second (probably the operating system's file cache).

## Allocation of the reading loop

The loop in [reading without allocation](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md) was measured over the CNES test file repeated to 204,000 records, as a `.dbf` and as a `.dbc`, leaving out what opening the reader allocates:

| Runtime | Counter | `.dbf` | `.dbc` | Garbage collections |
|---|---|---|---|---|
| .NET 10 | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET 9 (`netstandard2.0` build) | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET 8 (`netstandard2.0` build) | `GC.GetAllocatedBytesForCurrentThread` | 0 bytes | 4,120 bytes | none |
| .NET Framework 4.8 | `AppDomain.MonitoringTotalAllocatedMemorySize` | 0 bytes | 0 bytes | none |

The 4,120 bytes are a single 4 KB buffer at the end of the data, used to read what remains and verify the checksum. The .NET Framework counter advances by allocation blocks rather than per object, so it cannot show that buffer. The .NET 8 and .NET 9 rows were measured through the package, which those runtimes load as its `netstandard2.0` build, after a warm-up pass. On .NET 8, the loop allocates nothing, but the nullable forms of `GetFieldValue<T>` would: see [reading without allocation](https://github.com/euclydesuchoas/dbsus/blob/main/docs/reading.md).
