using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     Update and delete throughput.
/// </summary>
/// <remarks>
///     Seeding and loading happen in iteration setup so only the write is timed. Every mode pays
///     the same cost to get the rows into memory; what differs is turning N pending changes into
///     statements, which is the thing being compared.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class UpdateDeleteBenchmarks
{
    private BenchmarkDatabase _database = null!;

    private BenchmarkContext _stockContext = null!;
    private BenchmarkContext _bulkContext = null!;
    private List<Customer> _tracked = null!;
    private List<Customer> _trackedBulk = null!;
    private List<Customer> _detached = null!;

    [Params(10_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _database = BenchmarkDatabase.Start();

    [GlobalCleanup]
    public void Cleanup() => _database.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    ///     Reseeds both databases and stages the pending changes each mode will apply.
    /// </summary>
    /// <remarks>
    ///     A save consumes its pending changes, so contexts cannot be reused between invocations.
    ///     All three are prepared every iteration because BenchmarkDotNet does not tell setup which
    ///     benchmark is about to run; that cost sits outside the measurement.
    /// </remarks>
    [IterationSetup(Targets = [nameof(StockUpdate), nameof(TransparentUpdate), nameof(ExplicitBulkUpdate)])]
    public void SetupUpdate()
    {
        Reseed();

        foreach (var customer in _tracked)
        {
            customer.Balance += 1m;
        }

        foreach (var customer in _trackedBulk)
        {
            customer.Balance += 1m;
        }

        foreach (var customer in _detached)
        {
            customer.Balance += 1m;
        }
    }

    [IterationSetup(Targets = [nameof(StockDelete), nameof(TransparentDelete), nameof(ExplicitBulkDelete)])]
    public void SetupDelete()
    {
        Reseed();

        _stockContext.Customers.RemoveRange(_tracked);
        _bulkContext.Customers.RemoveRange(_trackedBulk);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _stockContext?.Dispose();
        _bulkContext?.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Update: SaveChanges (stock EF)")]
    public Task StockUpdate() => _stockContext.SaveChangesAsync();

    [Benchmark(Description = "Update: SaveChanges (EF.Toolkit.Bulk)")]
    public Task TransparentUpdate() => _bulkContext.SaveChangesAsync();

    [Benchmark(Description = "Update: BulkUpdateAsync")]
    public async Task ExplicitBulkUpdate()
    {
        await using var context = _database.BulkContext();
        await context.BulkUpdateAsync(_detached);
    }

    [Benchmark(Description = "Delete: SaveChanges (stock EF)")]
    public Task StockDelete() => _stockContext.SaveChangesAsync();

    [Benchmark(Description = "Delete: SaveChanges (EF.Toolkit.Bulk)")]
    public Task TransparentDelete() => _bulkContext.SaveChangesAsync();

    [Benchmark(Description = "Delete: BulkDeleteAsync")]
    public async Task ExplicitBulkDelete()
    {
        await using var context = _database.BulkContext();
        await context.BulkDeleteAsync(_detached);
    }

    private void Reseed()
    {
        _database.Seed(Rows);

        _stockContext = _database.StockContext();
        _bulkContext = _database.BulkContext();

        // Materialised and tracked up front so the query cost is not attributed to the write.
        _tracked = [.. _stockContext.Customers];
        _trackedBulk = [.. _bulkContext.Customers];

        using var reader = _database.BulkContext();
        _detached = [.. reader.Customers.AsNoTracking()];
    }
}
