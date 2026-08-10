using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     Guards for behaviour that was wrong in a way no existing test could see: the failures here
///     lost or corrupted data rather than merely running slowly.
/// </summary>
public abstract class CorrectnessTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    ///     A synchronise deletes every row its source does not contain. Split across batches, the
    ///     second batch's delete arm removed exactly what the first batch had just written, so the
    ///     table ended up holding only the final batch.
    /// </summary>
    [Fact]
    public async Task Synchronize_is_not_split_by_batch_size()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var desired = Customers(250);

        var result = await context.BulkSynchronizeAsync(
            desired,
            o => o.MatchOn(c => c.Email).BatchSize(50),
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(250);
        result.Deleted.ShouldBe(0);

        var stored = await context.Customers.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);

        // Before the fix this was 50 — the last batch — with the preceding 200 deleted.
        stored.ShouldBe(250);
    }

    /// <summary>
    ///     The same, with rows already present, so the delete arm has real work and a mid-run
    ///     delete of earlier batches would be visible in the counts as well as the contents.
    /// </summary>
    [Fact]
    public async Task Synchronize_across_batches_keeps_every_matched_row()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        await context.BulkInsertAsync(
            Customers(300), cancellationToken: TestContext.Current.CancellationToken);

        // Keep 200 of them, drop 100, add 40 new.
        var desired = Customers(200);
        desired.AddRange(Customers(40, startAt: 900));

        var result = await context.BulkSynchronizeAsync(
            desired,
            o => o.MatchOn(c => c.Email).BatchSize(64),
            TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(40);
        result.Updated.ShouldBe(200);
        result.Deleted.ShouldBe(100);

        var stored = await context.Customers.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);
        stored.ShouldBe(240);
    }

    /// <summary>
    ///     A store-generated column carrying a value converter has to run the converter in reverse
    ///     on the way back. Without it the entity ends up holding whatever the provider returned,
    ///     which for a strongly-typed key does not even round-trip through the same type.
    /// </summary>
    [Fact]
    public async Task Converted_generated_key_round_trips_onto_the_entity()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var shipments = Shipments(200);

        await context.BulkInsertAsync(
            shipments, cancellationToken: TestContext.Current.CancellationToken);

        shipments.ShouldAllBe(s => s.Id.Value > 0);
        shipments.Select(s => s.Id.Value).Distinct().Count().ShouldBe(200);

        // And the keys written back are the keys actually stored, not just plausible-looking ones.
        var stored = await context.Shipments.AsNoTracking()
            .ToDictionaryAsync(s => s.Id.Value, s => s.Code, TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(200);
        foreach (var shipment in shipments)
        {
            stored.ShouldContainKeyAndValue(shipment.Id.Value, shipment.Code);
        }
    }

    /// <summary>
    ///     The same column through the transparent path, which propagates values by a different
    ///     mechanism — EF's own result propagation rather than the explicit API's setters.
    /// </summary>
    [Fact]
    public async Task Converted_generated_key_round_trips_through_save_changes()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var shipments = Shipments(200);
        context.Shipments.AddRange(shipments);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        shipments.ShouldAllBe(s => s.Id.Value > 0);
        shipments.Select(s => s.Id.Value).Distinct().Count().ShouldBe(200);
    }

    /// <summary>
    ///     A per-call timeout has to survive the whole staged path — the bulk copy, the temp-table
    ///     DDL and the set-based statement are all separate commands, and every one of them used to
    ///     ignore it.
    /// </summary>
    /// <remarks>
    ///     This asserts the end-to-end plumbing accepts the setting and still produces a correct
    ///     result. Which value each command ends up with is
    ///     <c>BulkExecutionSettingsTests</c>'s job: the staging commands are raw ADO commands and
    ///     deliberately outside EF's interception pipeline, so there is nothing here that could
    ///     observe them without weakening the test into a timing race.
    /// </remarks>
    [Fact]
    public async Task A_per_call_timeout_survives_the_staged_path()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        await context.BulkInsertAsync(
            Customers(400),
            o => o.Timeout(TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        var loaded = await context.Customers.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        foreach (var customer in loaded)
        {
            customer.Name = customer.Name.Replace("Customer", "Renamed", StringComparison.Ordinal);
        }

        await context.BulkUpdateAsync(
            loaded,
            o => o.Timeout(TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);

        var renamed = await context.Customers.AsNoTracking()
            .CountAsync(c => c.Name.StartsWith("Renamed"), TestContext.Current.CancellationToken);

        renamed.ShouldBe(400);
    }

    private async Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();
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

    private static List<Shipment> Shipments(int count)
        => [.. Enumerable.Range(0, count).Select(i => new Shipment { Code = $"SHIP-{i:D6}" })];
}
