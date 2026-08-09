using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     Insert throughput: stock EF Core, transparent EF.Toolkit.Bulk, and the explicit API.
/// </summary>
/// <remarks>
///     The three are measured together because the interesting result is the gap between them.
///     Transparent mode replaces only how a batch is executed, so it still pays EF's change
///     detection and command materialisation; the explicit API skips that pipeline entirely.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class InsertBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private List<Customer> _customers = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _database = BenchmarkDatabase.Start();

    [GlobalCleanup]
    public void Cleanup() => _database.DisposeAsync().AsTask().GetAwaiter().GetResult();

    // Rebuilding the entities each iteration matters: an insert writes generated keys back onto
    // them, and a second run over the same objects would no longer be inserting fresh rows.
    [IterationSetup]
    public void IterationSetup()
    {
        _customers = Data.Customers(Rows);
        _database.Reset();
    }

    [Benchmark(Baseline = true, Description = "SaveChanges (stock EF)")]
    public async Task StockSaveChanges()
    {
        await using var context = _database.StockContext();
        context.Customers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "SaveChanges (EF.Toolkit.Bulk)")]
    public async Task TransparentSaveChanges()
    {
        await using var context = _database.BulkContext();
        context.Customers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "BulkInsertAsync")]
    public async Task ExplicitBulkInsert()
    {
        await using var context = _database.BulkContext();
        await context.BulkInsertAsync(_customers);
    }
}
