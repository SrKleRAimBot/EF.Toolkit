# After — PostgreSQL 16

Measured on this branch with the same harness and machine as `baseline-postgres16.md`.

Times on a laptop running database containers are noisy: stock EF itself measured 25% slower
between two runs of the same baseline commit, so ratios are the reliable signal and absolute
milliseconds are not. Allocations are deterministic and can be compared directly.

Allocation changes against the baseline, explicit API:

| Scenario | Baseline | After | Change |
| --- | ---: | ---: | ---: |
| Insert 10,000 x 5 columns | 1,794 KB | 1,277 KB | -29% |
| Insert 100,000 x 5 columns | 17,315 KB | 11,905 KB | -31% |
| Insert 10,000 x 30 columns | 7.90 MB | 3.03 MB | -62% |
| Insert 100,000 x 30 columns | 78.24 MB | 28.05 MB | -64% |

Update, delete and merge allocate the same as the baseline; their staged paths keep the boxed
writer because a staged column may carry a loaded value rather than a current one.

## InsertBenchmarks

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                          | Rows   | Mean        | Error      | StdDev     | Ratio | RatioSD | Rows/sec | Gen0      | Gen1      | Gen2      | Allocated    | Alloc Ratio |
|-------------------------------- |------- |------------:|-----------:|-----------:|------:|--------:|---------:|----------:|----------:|----------:|-------------:|------------:|
| **&#39;SaveChanges (stock EF)&#39;**        | **1000**   |    **61.94 ms** |  **13.702 ms** |  **12.817 ms** |  **1.04** |    **0.30** |   **16,145** |         **-** |         **-** |         **-** |   **8473.77 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 1000   |    32.71 ms |   3.142 ms |   2.624 ms |  0.55 |    0.12 |   30,569 |         - |         - |         - |   4411.22 KB |        0.52 |
| BulkInsertAsync                 | 1000   |    18.19 ms |   9.660 ms |   8.563 ms |  0.31 |    0.16 |   54,980 |         - |         - |         - |    218.64 KB |        0.03 |
|                                 |        |             |            |            |       |         |          |           |           |           |              |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **10000**  |   **368.80 ms** |  **55.306 ms** |  **49.027 ms** |  **1.02** |    **0.18** |   **27,115** |         **-** |         **-** |         **-** |  **77582.27 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 10000  |   132.29 ms |  31.598 ms |  28.011 ms |  0.36 |    0.09 |   75,593 |         - |         - |         - |  42902.34 KB |        0.55 |
| BulkInsertAsync                 | 10000  |    50.79 ms |   8.967 ms |   8.387 ms |  0.14 |    0.03 |  196,906 |         - |         - |         - |   1277.03 KB |        0.02 |
|                                 |        |             |            |            |       |         |          |           |           |           |              |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **100000** | **3,735.46 ms** | **198.843 ms** | **185.998 ms** |  **1.00** |    **0.07** |   **26,770** | **3000.0000** | **2000.0000** | **2000.0000** | **766270.45 KB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 100000 |   650.84 ms |  37.869 ms |  35.423 ms |  0.17 |    0.01 |  153,648 |         - |         - |         - |  421900.2 KB |        0.55 |
| BulkInsertAsync                 | 100000 |   310.21 ms |  30.680 ms |  28.698 ms |  0.08 |    0.01 |  322,360 |         - |         - |         - |  11904.78 KB |        0.02 |

## MergeBenchmarks

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                               | Rows  | Counts      | Mean     | Error     | StdDev   | Ratio | RatioSD | Rows/sec | Allocated | Alloc Ratio |
|------------------------------------- |------ |------------ |---------:|----------:|---------:|------:|--------:|---------:|----------:|------------:|
| **&#39;Read-then-decide, then SaveChanges&#39;** | **10000** | **Exact**       | **396.8 ms** | **103.40 ms** | **96.72 ms** |  **1.06** |    **0.35** |   **25,200** |  **65.72 MB** |        **1.00** |
| BulkMergeAsync                       | 10000 | Exact       | 139.3 ms |  36.50 ms | 34.14 ms |  0.37 |    0.12 |   71,779 |   8.18 MB |        0.12 |
|                                      |       |             |          |           |          |       |         |          |           |             |
| **&#39;Read-then-decide, then SaveChanges&#39;** | **10000** | **Approximate** | **372.6 ms** |  **75.20 ms** | **70.34 ms** |  **1.03** |    **0.26** |   **26,841** |   **66.1 MB** |        **1.00** |
| BulkMergeAsync                       | 10000 | Approximate | 134.2 ms |  25.46 ms | 22.57 ms |  0.37 |    0.09 |   74,517 |   8.18 MB |        0.12 |

## UpdateDeleteBenchmarks

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                                  | Rows  | Mean      | Error     | StdDev    | Ratio | RatioSD | Rows/sec | Allocated   | Alloc Ratio |
|---------------------------------------- |------ |----------:|----------:|----------:|------:|--------:|---------:|------------:|------------:|
| &#39;Update: SaveChanges (stock EF)&#39;        | 10000 | 349.92 ms | 44.352 ms | 39.317 ms |  1.01 |    0.16 |   28,578 | 47288.39 KB |       1.000 |
| &#39;Update: SaveChanges (EF.Toolkit.Bulk)&#39; | 10000 | 104.49 ms | 34.291 ms | 32.076 ms |  0.30 |    0.10 |   95,701 | 24950.65 KB |       0.528 |
| &#39;Update: BulkUpdateAsync&#39;               | 10000 |  85.83 ms | 13.135 ms | 10.255 ms |  0.25 |    0.04 |  116,510 |  1418.95 KB |       0.030 |
| &#39;Delete: SaveChanges (stock EF)&#39;        | 10000 | 254.73 ms | 40.758 ms | 38.125 ms |  0.74 |    0.14 |   39,258 | 37296.41 KB |       0.789 |
| &#39;Delete: SaveChanges (EF.Toolkit.Bulk)&#39; | 10000 |  39.74 ms |  7.436 ms |  6.592 ms |  0.11 |    0.02 |  251,665 | 24797.32 KB |       0.524 |
| &#39;Delete: BulkDeleteAsync&#39;               | 10000 |  25.77 ms |  5.843 ms |  5.466 ms |  0.07 |    0.02 |  388,112 |   395.13 KB |       0.008 |

## WideInsertBenchmarks

```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M3 Max, 1 CPU, 14 logical and 14 physical cores
.NET SDK 10.0.301
  [Host]     : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a
  Job-KQXXMH : .NET 10.0.9 (10.0.9, 10.0.926.27113), Arm64 RyuJIT armv8.0-a

InvocationCount=1  IterationCount=15  UnrollFactor=1  
WarmupCount=3  

```
| Method                          | Rows   | Mean        | Error     | StdDev    | Ratio | RatioSD | Rows/sec | Gen0       | Gen1       | Gen2       | Allocated  | Alloc Ratio |
|-------------------------------- |------- |------------:|----------:|----------:|------:|--------:|---------:|-----------:|-----------:|-----------:|-----------:|------------:|
| **&#39;SaveChanges (stock EF)&#39;**        | **10000**  |   **838.47 ms** |  **45.93 ms** |  **35.86 ms** |  **1.00** |    **0.06** |   **11,927** |  **2000.0000** |  **1000.0000** |  **1000.0000** |   **296.4 MB** |        **1.00** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 10000  |   122.28 ms |  18.14 ms |  16.08 ms |  0.15 |    0.02 |   81,782 |          - |          - |          - |   98.91 MB |        0.33 |
| BulkInsertAsync                 | 10000  |    56.15 ms |  15.22 ms |  11.88 ms |  0.07 |    0.01 |  178,079 |          - |          - |          - |    3.03 MB |        0.01 |
|                                 |        |             |           |           |       |         |          |            |            |            |            |             |
| **&#39;SaveChanges (stock EF)&#39;**        | **100000** | **9,019.23 ms** | **219.49 ms** | **194.57 ms** |  **1.00** |    **0.03** |   **11,087** | **12000.0000** | **11000.0000** | **11000.0000** | **2942.86 MB** |       **1.000** |
| &#39;SaveChanges (EF.Toolkit.Bulk)&#39; | 100000 | 1,206.53 ms |  35.53 ms |  33.23 ms |  0.13 |    0.00 |   82,882 |  2000.0000 |  1000.0000 |  1000.0000 |  981.89 MB |       0.334 |
| BulkInsertAsync                 | 100000 |   507.26 ms |  62.01 ms |  58.00 ms |  0.06 |    0.01 |  197,138 |          - |          - |          - |   28.05 MB |       0.010 |

