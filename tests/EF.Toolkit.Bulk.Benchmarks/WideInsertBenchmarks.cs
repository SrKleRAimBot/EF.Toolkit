using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     Insert throughput on a thirty-column row.
/// </summary>
/// <remarks>
///     The narrow benchmark measures how fast rows reach the wire; this one measures what each
///     column costs on the way. Per-cell work — boxing, type resolution, converter dispatch, the
///     double read a data reader can be asked for — is invisible at five columns and dominant at
///     thirty, which is closer to the schemas this library is actually pointed at.
/// </remarks>
[Config(typeof(BenchmarkConfig))]
public class WideInsertBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private List<WideCustomer> _customers = null!;

    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _database = BenchmarkDatabase.Start();

    [GlobalCleanup]
    public void Cleanup() => _database.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        _customers = Data.WideCustomers(Rows);
        _database.Reset();
    }

    [Benchmark(Baseline = true, Description = "SaveChanges (stock EF)")]
    public async Task StockSaveChanges()
    {
        await using var context = _database.StockContext();
        context.WideCustomers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "SaveChanges (EF.Toolkit.Bulk)")]
    public async Task TransparentSaveChanges()
    {
        await using var context = _database.BulkContext();
        context.WideCustomers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "BulkInsertAsync")]
    public async Task ExplicitBulkInsert()
    {
        await using var context = _database.BulkContext();
        await context.BulkInsertAsync(_customers);
    }
}
