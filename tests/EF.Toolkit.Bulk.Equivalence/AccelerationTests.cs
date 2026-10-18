using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     Asserts that the shapes every engine is meant to accelerate are in fact accelerated.
/// </summary>
/// <remarks>
///     The rest of the suite compares results, and results match just as well when a partition
///     quietly falls back to stock EF — so an executor that declined everything would pass all of
///     it. These scenarios are the ones each engine's executor claims to handle, and here a fallback
///     is the failure.
/// </remarks>
public abstract class AccelerationTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_explicit_api_takes_the_fast_path_for_every_operation()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();
        await using var context = fixture.CreateBulkContext();
        var ct = TestContext.Current.CancellationToken;

        // A store-generated key to read back, which is the shape most likely to decline.
        var customers = Customers(300);
        (await context.BulkInsertAsync(customers, cancellationToken: ct)).Inserted.ShouldBe(300);
        customers.ShouldAllBe(c => c.Id > 0);
        customers.Select(c => c.Id).Distinct().Count().ShouldBe(300);

        foreach (var customer in customers)
        {
            customer.Name += " (updated)";
        }

        await context.BulkUpdateAsync(customers, cancellationToken: ct);

        // Half already there and half new, matched on something other than the generated key, so
        // the existing half has to learn its key from the database.
        var incoming = Customers(300, startAt: 150);
        var merged = await context.BulkMergeAsync(incoming, o => o.MatchOn(c => c.Email), ct);

        merged.Inserted.ShouldBe(150);
        merged.Updated.ShouldBe(150);
        incoming.ShouldAllBe(c => c.Id > 0);

        var desired = Customers(200, startAt: 100);
        var synced = await context.BulkSynchronizeAsync(
            desired, o => o.MatchOn(c => c.Email).AllowFullTableDelete(), ct);

        synced.Deleted.ShouldBe(250);

        await context.BulkDeleteAsync(desired, cancellationToken: ct);

        (await context.Customers.CountAsync(ct)).ShouldBe(0);

        recorder.ExplicitFallbacks.ShouldBeEmpty(
            string.Join("; ", recorder.ExplicitFallbacks.Select(f => f.Reason)));
    }

    [Fact]
    public async Task Staged_operations_hold_when_the_staging_table_is_indexed()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        // A threshold of one indexes every staging table, so the branch that only large operations
        // reach in practice runs here on every engine: on MySQL the index is declared with the
        // table, everywhere else it is built after the load.
        using var recorder = new PartitionRecorder();
        await using var context = fixture.CreateBulkContext(b => b.StagingIndexThreshold(1));
        var ct = TestContext.Current.CancellationToken;

        var customers = Customers(200);
        await context.BulkInsertAsync(customers, cancellationToken: ct);

        foreach (var customer in customers)
        {
            customer.Name += " (updated)";
        }

        await context.BulkUpdateAsync(customers, cancellationToken: ct);

        var merged = await context.BulkMergeAsync(
            Customers(200, startAt: 100), o => o.MatchOn(c => c.Email), ct);

        merged.Inserted.ShouldBe(100);
        merged.Updated.ShouldBe(100);

        var synced = await context.BulkSynchronizeAsync(
            Customers(50), o => o.MatchOn(c => c.Email).AllowFullTableDelete(), ct);

        synced.Deleted.ShouldBe(250);

        await context.BulkDeleteAsync(
            await context.Customers.AsNoTracking().ToListAsync(ct), cancellationToken: ct);

        (await context.Customers.CountAsync(ct)).ShouldBe(0);
        recorder.ExplicitFallbacks.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveChanges_takes_the_fast_path_for_inserts_updates_and_deletes()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();
        await using var context = fixture.CreateBulkContext();
        var ct = TestContext.Current.CancellationToken;

        var customers = Customers(300);
        context.Customers.AddRange(customers);
        await context.SaveChangesAsync(ct);

        foreach (var customer in customers)
        {
            customer.Name += " (updated)";
        }

        await context.SaveChangesAsync(ct);

        context.Customers.RemoveRange(customers);
        await context.SaveChangesAsync(ct);

        var declined = recorder.Executions
            .Where(e => !e.Accelerated && !e.Partition.BelowThreshold)
            .ToList();

        declined.ShouldBeEmpty(string.Join("; ", declined.Select(e => e.FallbackReason)));
        recorder.Executions.Count(e => e.Accelerated).ShouldBe(3);
    }

    private static List<Customer> Customers(int count, int startAt = 0)
        =>
        [
            .. Enumerable.Range(startAt, count).Select(i => new Customer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                CreatedAt = Epoch.AddMinutes(i)
            })
        ];
}
