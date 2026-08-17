using System.Globalization;
using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Equivalence.Infrastructure;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence;

/// <summary>
///     What the explicit bulk API produces in the audit table.
/// </summary>
/// <remarks>
///     These operations bypass the change tracker entirely, so none of them is visible to the
///     <c>SaveChanges</c> interceptor that audits everything else. Everything asserted here comes
///     from the observer seam and the before-image read instead.
/// </remarks>
public abstract class BulkAuditTests(AuditDatabaseFixture fixture)
{
    [Fact]
    public async Task Bulk_insert_records_an_entry_per_row_with_generated_keys()
    {
        await ResetAsync();

        var products = Products(50);

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkInsertAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(50);
        entries.ShouldAllBe(e => e.Operation == (int)AuditOperation.Insert);
        entries.ShouldAllBe(e => e.Source == AuditSources.BulkInsert);

        // Written back onto the entities by the insert, and into the entries by the observer.
        products.ShouldAllBe(p => p.Id > 0);
        entries.Select(e => e.EntityKey).Order(StringComparer.Ordinal)
            .ShouldBe(products.Select(p => p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Bulk_update_records_old_and_new_values()
    {
        await ResetAsync();

        var products = Products(20);
        await SeedAsync(products);
        await ClearAuditAsync();

        foreach (var product in products)
        {
            product.Name = "Renamed";
            product.Status = ProductStatus.Live;
        }

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkUpdateAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);
        entries.Count.ShouldBe(20);

        var payload = Payload(entries[0]);
        payload.GetProperty("op").GetString().ShouldBe("update");

        // The whole point of the before-image read: a detached object carries no earlier state, so
        // without it there would be no "old" half at all.
        payload.GetProperty("old").GetProperty("Name").GetString().ShouldBe("Widget");
        payload.GetProperty("new").GetProperty("Name").GetString().ShouldBe("Renamed");
        payload.GetProperty("old").GetProperty("Status").GetString().ShouldBe("Draft");
        payload.GetProperty("new").GetProperty("Status").GetString().ShouldBe("Live");
    }

    [Fact]
    public async Task Bulk_delete_records_the_rows_as_they_were()
    {
        await ResetAsync();

        var products = Products(10);
        await SeedAsync(products);
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkDeleteAsync(
                products, cancellationToken: TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(10);
        entries.ShouldAllBe(e => e.Operation == (int)AuditOperation.Delete);

        // A delete's row set knows only the key, so every other column here came from the
        // before-image read rather than from the operation itself.
        Payload(entries[0]).GetProperty("old").GetProperty("Name").GetString().ShouldBe("Widget");
    }

    [Fact]
    public async Task Bulk_merge_records_inserts_and_updates_separately()
    {
        await ResetAsync();

        var existing = Products(5);
        await SeedAsync(existing);
        await ClearAuditAsync();

        foreach (var product in existing)
        {
            product.Name = "Renamed";
        }

        var merged = existing.Concat(Products(5, startAt: 100)).ToList();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkMergeAsync(
                merged,
                o => o.MatchOn(p => p.Sku),
                TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        // Which rows the merge inserted and which it updated is not something the statement reports
        // per row on every engine — it is derived from the before-image read matching, or not.
        entries.Count(e => e.Operation == (int)AuditOperation.Insert).ShouldBe(5);
        entries.Count(e => e.Operation == (int)AuditOperation.Update).ShouldBe(5);
        entries.ShouldAllBe(e => e.Source == AuditSources.BulkMerge);
    }

    [Fact]
    public async Task Bulk_synchronise_records_the_rows_its_delete_arm_removed()
    {
        await ResetAsync();

        await SeedAsync(Products(10));
        await ClearAuditAsync();

        // Four of the ten survive; the other six are removed by the delete arm and correspond to
        // nothing in the source at all.
        var kept = Products(4);

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkSynchronizeAsync(
                kept,
                o => o.MatchOn(p => p.Sku).AllowFullTableDelete(),
                TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);
        var deletes = entries.Where(e => e.Operation == (int)AuditOperation.Delete).ToList();

        deletes.Count.ShouldBe(6);
        deletes.ShouldAllBe(e => e.Source == AuditSources.BulkSynchronize);

        // Recorded from the pre-read, since no entity the caller passed in describes them.
        Payload(deletes[0]).GetProperty("old").GetProperty("Name").GetString().ShouldBe("Widget");
    }

    [Fact]
    public async Task Writes_no_entries_when_the_call_opts_out()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkInsertAsync(
                Products(20),
                o => o.WithoutObservers(),
                TestContext.Current.CancellationToken);
        }

        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Refuses_a_merge_that_cannot_tell_inserts_from_updates()
    {
        await ResetAsync();

        await using var context = fixture.CreateContext(bulk: true);

        var exception = await Should.ThrowAsync<AuditNotSupportedException>(
            () => context.BulkMergeAsync(
                Products(5),
                o => o.MatchOn(p => p.Sku).WithoutBeforeImages(),
                TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("which of them the operation inserted");
    }

    [Fact]
    public async Task Writes_entries_in_the_same_transaction_as_the_rows()
    {
        await ResetAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(TestContext.Current.CancellationToken);

            await context.BulkInsertAsync(
                Products(10), cancellationToken: TestContext.Current.CancellationToken);

            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        }

        // Rolled back together, or the trail would claim a change that never happened.
        (await AuditSnapshot.ReadAsync(fixture)).ShouldBeEmpty();

        await using var check = fixture.CreateContext(auditing: false);
        (await check.Products.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Bulk_update_records_columns_the_driver_reads_as_another_type()
    {
        await ResetAsync();

        var shifts = Shifts(5);
        await SeedAsync(shifts);
        await ClearAuditAsync();

        foreach (var shift in shifts)
        {
            shift.Name = "Late";
            shift.Date = shift.Date.AddDays(1);
            shift.StartsAt = shift.StartsAt.AddHours(4);
            shift.RecordedAt = shift.RecordedAt.AddDays(1);
        }

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkUpdateAsync(
                shifts, cancellationToken: TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);
        entries.Count.ShouldBe(5);

        var entry = entries.Single(e => e.EntityKey == shifts[0].Id.ToString(Invariant));
        var payload = Payload(entry);

        // The old halves are the before-image read, in the CLR types the model declares rather than
        // the ones the driver reaches for. Before that read asked for them by type, every column
        // here failed on a database whose driver disagrees about one of them.
        payload.GetProperty("old").GetProperty("Date").GetString().ShouldBe("2026-03-02");
        payload.GetProperty("new").GetProperty("Date").GetString().ShouldBe("2026-03-03");

        payload.GetProperty("old").GetProperty("StartsAt").GetString().ShouldStartWith("09:30");
        payload.GetProperty("new").GetProperty("StartsAt").GetString().ShouldStartWith("13:30");

        DateTimeOffset.Parse(
                payload.GetProperty("old").GetProperty("RecordedAt").GetString()!, Invariant)
            .ShouldBe(RecordedAt(1));

        DateTimeOffset.Parse(
                payload.GetProperty("new").GetProperty("RecordedAt").GetString()!, Invariant)
            .ShouldBe(RecordedAt(1).AddDays(1));
    }

    [Fact]
    public async Task Bulk_delete_records_such_a_row_as_it_was()
    {
        await ResetAsync();

        var shifts = Shifts(3);
        await SeedAsync(shifts);
        await ClearAuditAsync();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkDeleteAsync(
                shifts, cancellationToken: TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        entries.Count.ShouldBe(3);
        entries.ShouldAllBe(e => e.Operation == (int)AuditOperation.Delete);

        // A delete's row set knows only the key, so every column below came from the read alone.
        var payload = Payload(entries.Single(e => e.EntityKey == shifts[0].Id.ToString(Invariant)));

        payload.GetProperty("old").GetProperty("Date").GetString().ShouldBe("2026-03-02");
        payload.GetProperty("old").GetProperty("StartsAt").GetString().ShouldStartWith("09:30");

        DateTimeOffset.Parse(
                payload.GetProperty("old").GetProperty("RecordedAt").GetString()!, Invariant)
            .ShouldBe(RecordedAt(1));
    }

    [Fact]
    public async Task Bulk_merge_tells_inserts_from_updates_for_such_an_entity()
    {
        await ResetAsync();

        var existing = Shifts(3);
        await SeedAsync(existing);
        await ClearAuditAsync();

        foreach (var shift in existing)
        {
            shift.Name = "Late";
        }

        var merged = existing.Concat(Shifts(2, startAt: 100)).ToList();

        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkMergeAsync(
                merged,
                o => o.MatchOn(s => s.Code),
                TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);

        // The split comes from the before-image read matching or not, so a read that could not
        // capture these rows at all could not report it.
        entries.Count(e => e.Operation == (int)AuditOperation.Insert).ShouldBe(2);
        entries.Count(e => e.Operation == (int)AuditOperation.Update).ShouldBe(3);
    }

    [Fact]
    public async Task Bulk_synchronise_records_such_rows_when_its_delete_arm_removes_them()
    {
        await ResetAsync();

        await SeedAsync(Shifts(5));
        await ClearAuditAsync();

        // Two of the five survive; the other three correspond to nothing in the source, so the
        // before-image read is the only thing that ever sees them.
        await using (var context = fixture.CreateContext(bulk: true))
        {
            await context.BulkSynchronizeAsync(
                Shifts(2),
                o => o.MatchOn(s => s.Code).AllowFullTableDelete(),
                TestContext.Current.CancellationToken);
        }

        var entries = await AuditSnapshot.ReadAsync(fixture);
        var deletes = entries.Where(e => e.Operation == (int)AuditOperation.Delete).ToList();

        deletes.Count.ShouldBe(3);
        deletes.ShouldAllBe(e => e.Source == AuditSources.BulkSynchronize);

        Payload(deletes[0]).GetProperty("old").GetProperty("StartsAt").GetString()
            .ShouldStartWith("09:30");
    }

    /// <summary>Empties every table, audit entries included.</summary>
    protected Task ResetAsync()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        return fixture.ResetAsync();
    }

    /// <summary>Removes the entries a seeding step produced, so a scenario starts clean.</summary>
    protected async Task ClearAuditAsync()
    {
        var table = $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultSchema)}."
            + $"{fixture.Quote(Audit.Configuration.AuditOptions.DefaultTableName)}";

        await using var connection = fixture.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table}";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Writes rows without auditing them, so a scenario can start from existing data.</summary>
    protected async Task SeedAsync(IEnumerable<Product> products)
    {
        await using var context = fixture.CreateContext(auditing: false);
        context.Products.AddRange(products);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="SeedAsync(IEnumerable{Product})" />
    protected async Task SeedAsync(IEnumerable<Shift> shifts)
    {
        await using var context = fixture.CreateContext(auditing: false);
        context.Shifts.AddRange(shifts);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>The payload of an entry, parsed.</summary>
    protected static JsonElement Payload(AuditRow entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonDocument.Parse(entry.Changes).RootElement;
    }

    /// <summary>A run of products with distinct SKUs.</summary>
    protected static List<Product> Products(int count, int startAt = 1)
        => [.. Enumerable.Range(startAt, count).Select(i => new Product
        {
            Sku = $"SKU-{i}",
            Name = "Widget",
            Price = 9.99m,
            Status = ProductStatus.Draft,
            TenantId = "acme",
        })];

    /// <summary>A run of shifts with distinct codes.</summary>
    protected static List<Shift> Shifts(int count, int startAt = 1)
        => [.. Enumerable.Range(startAt, count).Select(i => new Shift
        {
            Code = $"SHIFT-{i}",
            Name = "Early",

            // Fixed rather than derived from the row, so what a before-image should have read is
            // one value the assertions can name.
            Date = new DateOnly(2026, 3, 2),
            StartsAt = new TimeOnly(9, 30),
            RecordedAt = RecordedAt(1),
        })];

    /// <summary>
    ///     A UTC instant, which is the only offset PostgreSQL's <c>timestamptz</c> accepts.
    /// </summary>
    protected static DateTimeOffset RecordedAt(int day)
        => new(2026, 3, day, 8, 0, 0, TimeSpan.Zero);

    private static CultureInfo Invariant => CultureInfo.InvariantCulture;
}
