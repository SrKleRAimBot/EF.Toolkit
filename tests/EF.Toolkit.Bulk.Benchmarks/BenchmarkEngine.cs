namespace EFToolkit.Bulk.Benchmarks;

/// <summary>Which database engine the benchmarks run against.</summary>
/// <remarks>
///     Chosen by the <c>EFBULK_BENCH_ENGINE</c> environment variable rather than a
///     <c>[Params]</c> dimension. As a parameter it would double every benchmark and hold two
///     containers open at once; the numbers are also not comparable across engines, so a single
///     table mixing them would invite exactly the comparison it cannot support. Run the suite once
///     per engine instead.
/// </remarks>
internal enum BenchmarkEngine
{
    PostgreSql,
    SqlServer
}

internal static class BenchmarkEngineSelection
{
    public const string Variable = "EFBULK_BENCH_ENGINE";

    /// <summary>The engine to run against, defaulting to PostgreSQL.</summary>
    public static BenchmarkEngine Current { get; } =
        Environment.GetEnvironmentVariable(Variable)?.Trim().ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" => BenchmarkEngine.SqlServer,
            _ => BenchmarkEngine.PostgreSql
        };
}
