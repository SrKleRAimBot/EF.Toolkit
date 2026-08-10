using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     Shared configuration: memory diagnostics, a rows-per-second column, and enough iterations
///     for a database-bound measurement to mean something.
/// </summary>
/// <remarks>
///     Each of these benchmarks resets and re-seeds a real database per iteration, so
///     <c>[IterationSetup]</c> forces one invocation per measurement and the usual amortisation
///     over many calls is unavailable. Databases also have long tails — checkpoints, autovacuum,
///     page splits — so a handful of iterations produces error bars wider than the differences
///     being measured. More iterations is the only fix available.
/// </remarks>
internal sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(3)
            .WithIterationCount(15)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));

        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumn(RowsPerSecondColumn.Instance);
        AddColumnProvider(DefaultColumnProviders.Instance);
    }
}

/// <summary>
///     Reports throughput, so results are comparable across row counts instead of leaving the
///     reader to divide a millisecond figure by a parameter.
/// </summary>
internal sealed class RowsPerSecondColumn : IColumn
{
    public static RowsPerSecondColumn Instance { get; } = new();

    public string Id => nameof(RowsPerSecondColumn);
    public string ColumnName => "Rows/sec";
    public string Legend => "Rows written per second, derived from the mean and the Rows parameter";

    public bool AlwaysShow => true;
    public ColumnCategory Category => ColumnCategory.Custom;
    public int PriorityInCategory => 0;
    public bool IsNumeric => true;
    public UnitType UnitType => UnitType.Dimensionless;

    public bool IsAvailable(Summary summary) => true;
    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase)
    {
        var rows = benchmarkCase.Parameters.Items
            .FirstOrDefault(p => p.Name == "Rows")?.Value;

        if (rows is not int count || count <= 0)
        {
            return "-";
        }

        var report = summary[benchmarkCase];
        if (report?.ResultStatistics is not { } statistics || statistics.Mean <= 0)
        {
            return "-";
        }

        // Mean is in nanoseconds.
        var perSecond = count / (statistics.Mean / 1_000_000_000d);

        return perSecond.ToString("N0", CultureInfo.InvariantCulture);
    }

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
        => GetValue(summary, benchmarkCase);

    public override string ToString() => ColumnName;
}
