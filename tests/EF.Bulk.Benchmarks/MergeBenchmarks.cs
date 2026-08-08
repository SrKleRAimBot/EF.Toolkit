using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;

namespace EFBulk.Benchmarks;

/// <summary>
///     Upsert throughput: the read-then-decide loop people write by hand, versus a single
///     database-side merge.
/// </summary>
/// <remarks>
///     EF Core has no upsert, so the baseline is what an application actually does — load the
///     existing rows, decide per item whether to add or update, save. Besides being slower, that
///     pattern races: another writer can insert between the read and the write. The merge cannot,
///     because the database makes the decision.
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class MergeBenchmarks
{
    private BenchmarkDatabase _database = null!;
    private List<Customer> _incoming = null!;

    [Params(10_000)]
    public int Rows { get; set; }

    /// <summary>How many of the incoming rows already exist. The rest are inserts.</summary>
    private int Existing => Rows / 2;

    [GlobalSetup]
    public void Setup() => _database = BenchmarkDatabase.Start();

    [GlobalCleanup]
    public void Cleanup() => _database.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [IterationSetup]
    public void IterationSetup()
    {
        _database.Seed(Existing);

        // Half overlap the seeded rows by email, half are new — so both arms of the upsert are
        // exercised rather than degenerating into a pure insert or a pure update.
        _incoming = Data.Customers(Rows);
        foreach (var customer in _incoming)
        {
            customer.Balance += 100m;
        }
    }

    [Benchmark(Baseline = true, Description = "Read-then-decide, then SaveChanges")]
    public async Task ManualUpsert()
    {
        await using var context = _database.StockContext();

        var existing = await context.Customers.ToDictionaryAsync(c => c.Email);

        foreach (var incoming in _incoming)
        {
            if (existing.TryGetValue(incoming.Email, out var current))
            {
                current.Name = incoming.Name;
                current.Balance = incoming.Balance;
                current.CreatedAt = incoming.CreatedAt;
            }
            else
            {
                context.Customers.Add(incoming);
            }
        }

        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "BulkMergeAsync")]
    public async Task BulkMerge()
    {
        await using var context = _database.BulkContext();
        await context.BulkMergeAsync(_incoming, o => o.MatchOn(c => c.Email));
    }
}
