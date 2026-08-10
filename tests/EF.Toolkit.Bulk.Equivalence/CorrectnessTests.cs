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

    /// <summary>
    ///     Two source rows carrying the same key. Correlation used to be keyed on those values, so
    ///     the pair collapsed into one entry and the operation reported one row updated out of two
    ///     without saying anything was wrong.
    /// </summary>
    [Fact]
    public async Task Duplicate_keys_in_an_update_list_are_reported_rather_than_silently_dropped()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        await context.BulkInsertAsync(
            Customers(200), cancellationToken: TestContext.Current.CancellationToken);

        var loaded = await context.Customers.AsNoTracking()
            .OrderBy(c => c.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        // The same row twice, with different values, plus the rest once.
        var first = loaded[0];
        var duplicate = new Customer
        {
            Id = first.Id,
            Name = "Second write",
            Email = first.Email,
            CreatedAt = first.CreatedAt
        };

        first.Name = "First write";

        var withDuplicate = new List<Customer>(loaded) { duplicate };

        var conflict = await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => context.BulkUpdateAsync(
                withDuplicate, cancellationToken: TestContext.Current.CancellationToken));

        // The set-based join applies one of the two, so the other genuinely did not take effect.
        // Saying so is the point: the previous behaviour returned a count one short and left the
        // caller to notice.
        conflict.Message.ShouldContain("expected to affect");
    }

    /// <summary>
    ///     PostgreSQL 17 upserts through <c>MERGE</c> where earlier versions use
    ///     <c>ON CONFLICT</c>. The two paths must be indistinguishable from outside — same rows,
    ///     same counts, same generated keys.
    /// </summary>
    /// <remarks>
    ///     On PostgreSQL 16 and SQL Server both arms run the same path, so this costs a little time
    ///     and proves nothing; on 17 it is the only thing comparing the new statement against the
    ///     old one on identical input.
    /// </remarks>
    [Fact]
    public async Task Both_upsert_paths_produce_the_same_result()
    {
        var byDefault = await MergeAndSnapshotAsync(useMerge: null);
        var forcedOld = await MergeAndSnapshotAsync(useMerge: false);

        byDefault.ShouldBe(forcedOld);
    }

    private async Task<string> MergeAndSnapshotAsync(bool? useMerge)
    {
        await ResetAsync();

        await using var context = fixture.CreateBulkContext(
            b =>
            {
                if (useMerge is { } value)
                {
                    b.UseMerge(value);
                }
            });

        await context.BulkInsertAsync(
            Customers(200), cancellationToken: TestContext.Current.CancellationToken);

        // Half overlap the existing rows, half are new.
        var incoming = Customers(100);
        foreach (var customer in incoming)
        {
            customer.Name += " (merged)";
        }

        incoming.AddRange(Customers(100, startAt: 900));

        var result = await context.BulkMergeAsync(
            incoming, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        // Generated keys have to land on the rows that were actually inserted.
        var keyed = incoming.Count(c => c.Id > 0);

        var stored = await context.Customers.AsNoTracking()
            .OrderBy(c => c.Email)
            .Select(c => c.Email + "|" + c.Name)
            .ToListAsync(TestContext.Current.CancellationToken);

        return $"{result.Inserted}/{result.Updated}/{result.Deleted} keys={keyed} "
            + string.Join(",", stored);
    }

    /// <summary>
    ///     A CHECK constraint has to reject the same rows whether or not the write was
    ///     accelerated. SqlBulkCopy skips constraint validation by default, so an accelerated
    ///     insert would have accepted rows stock EF rejects — and left the constraint untrusted,
    ///     which the optimiser then ignores for every later query against the table.
    /// </summary>
    [Fact]
    public async Task A_check_constraint_rejects_bulk_inserted_rows()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var sensor = new Sensor { Name = "S1" };
        await context.BulkInsertAsync(
            new List<Sensor> { sensor }, cancellationToken: TestContext.Current.CancellationToken);

        var readings = Enumerable.Range(0, 200)
            .Select(i => new Reading { SensorId = sensor.Id, Value = i, Label = "ok" })
            .ToList();

        readings[100].Value = -1;

        await Should.ThrowAsync<Exception>(
            () => context.BulkInsertAsync(
                readings, cancellationToken: TestContext.Current.CancellationToken));

        (await context.Readings.AsNoTracking()
                .CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    /// <summary>
    ///     A foreign key likewise. Without validation a bulk copy will happily write a row
    ///     referencing a principal that does not exist.
    /// </summary>
    [Fact]
    public async Task A_foreign_key_rejects_bulk_inserted_rows()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var orphans = Enumerable.Range(0, 200)
            .Select(i => new Reading { SensorId = 987_654, Value = i })
            .ToList();

        await Should.ThrowAsync<Exception>(
            () => context.BulkInsertAsync(
                orphans, cancellationToken: TestContext.Current.CancellationToken));

        (await context.Readings.AsNoTracking()
                .CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    /// <summary>
    ///     Nulls written into a column that has a database default must land exactly as stock EF
    ///     leaves them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Stated as a differential rather than a fixed expectation, because the expectation is
    ///         not obvious: EF omits a property from the insert when it holds the CLR default and
    ///         the column has a store default, so the database's default applies rather than the
    ///         null. A bulk copy has its own opinion — it substitutes column defaults for nulls
    ///         unless told not to — and what matters is only that the two agree.
    ///     </para>
    ///     <para>
    ///         Each case uses a uniform value so the batch holds a single column shape. Mixing them
    ///         gives the table two shapes, which partitions into two groups and hands out identity
    ///         values in a different order from stock EF — a real difference, but a different one,
    ///         and covered separately.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("labelled")]
    public async Task Nulls_against_a_defaulted_column_match_stock_ef(string? label)
        => await Differential.AssertAsync(
            fixture,
            async context =>
            {
                var sensor = new Sensor { Name = "S1" };
                context.Sensors.Add(sensor);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                context.Readings.AddRange(
                    Enumerable.Range(0, 200).Select(i => new Reading
                    {
                        SensorId = sensor.Id,
                        Value = i,
                        Label = label
                    }));

                await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            });

    /// <summary>
    ///     Rows of the same table but different column shape — here a nullable column with a
    ///     database default, set on some rows and left null on others — are grouped and written
    ///     shape by shape, so store-generated keys are handed out in a different order from stock
    ///     EF. Each row still gets its own key and its own values; only the numbering differs.
    /// </summary>
    [Fact]
    public async Task Mixed_column_shapes_keep_every_row_intact()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var sensor = new Sensor { Name = "S1" };
        context.Sensors.Add(sensor);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        context.Readings.AddRange(
            Enumerable.Range(0, 200).Select(i => new Reading
            {
                SensorId = sensor.Id,
                Value = i,
                Label = i % 2 == 0 ? null : "labelled"
            }));

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await context.Readings.AsNoTracking()
            .ToDictionaryAsync(r => r.Value, r => r.Label, TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(200);

        // Every row kept the label it was given -- the default where it was null, the value
        // otherwise -- and no row was lost or duplicated by the grouping.
        for (var i = 0; i < 200; i++)
        {
            stored[i].ShouldBe(i % 2 == 0 ? "unlabelled" : "labelled");
        }
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
