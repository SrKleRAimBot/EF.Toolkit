# Baseline — PostgreSQL 16

Measured at commit `edec386` (before this branch) using the harness in this project, so the
before/after comparison is like for like. Reproduce with:

```bash
dotnet run --project tests/EF.Toolkit.Bulk.Benchmarks -c Release -- --filter "*Insert*"
```

Set `EFBULK_BENCH_ENGINE=sqlserver` to run the same suite against SQL Server.

> These numbers supersede the ones previously quoted in the README, which were taken with one
> warmup and five iterations. That was not enough for a database-bound measurement — the reported
> error exceeded the mean in several rows, and transparent mode in particular was being measured
> before it had warmed up.

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                          | Rows   | Mean        | Error     | StdDev    | Ratio | RatioSD | Rows/sec | Gen0      | Gen1      | Gen2      | Allocated    | Alloc Ratio |
|-------------------------------- |------- |------------:|----------:|----------:|------:|--------:|---------:|----------:|----------:|----------:|-------------:|------------:|
| **&#39;SaveChanges (stock EF)&#39;**        | **1000**   |    **50.50 ms** |  **7.976 ms** |  **7.071 ms** |  **1.02** |    **0.19** |   **19,801** |         **-** |         **-** |         **-** |   **8473.77 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 1000   |    29.76 ms |  2.468 ms |  2.061 ms |  0.60 |    0.09 |   33,606 |         - |         - |         - |   4387.05 KB |        0.52 |
| BulkInsertAsync                 | 1000   |    11.21 ms |  2.457 ms |  2.178 ms |  0.23 |    0.05 |   89,244 |         - |         - |         - |    243.75 KB |        0.03 |
|                                 |        |             |           |           |       |         |          |           |           |           |              |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **10000**  |   **276.55 ms** | **19.708 ms** | **17.470 ms** |  **1.00** |    **0.09** |   **36,160** |         **-** |         **-** |         **-** |  **77582.27 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 10000  |    67.49 ms | 20.402 ms | 15.928 ms |  0.24 |    0.06 |  148,179 |         - |         - |         - |  42667.23 KB |        0.55 |
| BulkInsertAsync                 | 10000  |    47.16 ms |  2.387 ms |  2.116 ms |  0.17 |    0.01 |  212,041 |         - |         - |         - |   1793.73 KB |        0.02 |
|                                 |        |             |           |           |       |         |          |           |           |           |              |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **100000** | **2,987.77 ms** | **99.771 ms** | **88.445 ms** |  **1.00** |    **0.04** |   **33,470** | **3000.0000** | **2000.0000** | **2000.0000** | **766277.27 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 100000 |   601.73 ms | 16.858 ms | 14.077 ms |  0.20 |    0.01 |  166,188 | 2000.0000 | 1000.0000 | 1000.0000 | 419561.15 KB |        0.55 |
| BulkInsertAsync                 | 100000 |   269.61 ms |  8.320 ms |  7.783 ms |  0.09 |    0.00 |  370,910 |         - |         - |         - |  17315.07 KB |        0.02 |

## Wide table (30 columns)

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                          | Rows   | Mean        | Error      | StdDev     | Median      | Ratio | RatioSD | Rows/sec | Gen0       | Gen1       | Gen2       | Allocated  | Alloc Ratio |
|-------------------------------- |------- |------------:|-----------:|-----------:|------------:|------:|--------:|---------:|-----------:|-----------:|-----------:|-----------:|------------:|
| **&#39;SaveChanges (stock EF)&#39;**        | **10000**  |   **755.14 ms** |  **19.812 ms** |  **17.563 ms** |   **753.16 ms** |  **1.00** |    **0.03** |   **13,243** |  **2000.0000** |  **1000.0000** |  **1000.0000** |  **296.79 MB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 10000  |   107.29 ms |   8.791 ms |   6.864 ms |   105.36 ms |  0.14 |    0.01 |   93,203 |          - |          - |          - |   98.68 MB |        0.33 |
| BulkInsertAsync                 | 10000  |    97.08 ms |  66.202 ms |  61.925 ms |    72.27 ms |  0.13 |    0.08 |  103,003 |          - |          - |          - |    7.91 MB |        0.03 |
|                                 |        |             |            |            |             |       |         |          |            |            |            |            |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **100000** | **7,916.93 ms** | **176.762 ms** | **156.695 ms** | **7,850.86 ms** |  **1.00** |    **0.03** |   **12,631** | **12000.0000** | **11000.0000** | **11000.0000** | **2942.88 MB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 100000 | 1,165.27 ms |  19.255 ms |  17.069 ms | 1,167.75 ms |  0.15 |    0.00 |   85,817 |  2000.0000 |  1000.0000 |  1000.0000 |   979.6 MB |        0.33 |
| BulkInsertAsync                 | 100000 |   409.58 ms |  19.628 ms |  17.400 ms |   405.32 ms |  0.05 |    0.00 |  244,154 |          - |          - |          - |   78.24 MB |        0.03 |
