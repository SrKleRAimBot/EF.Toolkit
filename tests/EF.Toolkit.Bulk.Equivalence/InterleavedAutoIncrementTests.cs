using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     What MySQL's default AUTO_INCREMENT lock mode changes about the fast path: under it a
///     multi-row insert's keys cannot be inferred from the first, so each row reports its own.
/// </summary>
public abstract class InterleavedAutoIncrementTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task An_insert_needing_its_keys_back_is_accelerated_one_statement_per_row()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();
        await using var context = fixture.CreateBulkContext();

        var customers = Enumerable.Range(0, 200).Select(i => new Customer
        {
            Name = $"Customer {i}",
            Email = $"customer{i}@example.com",
        }).ToList();

        var result = await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(200);
        customers.Select(c => c.Id).Distinct().Count().ShouldBe(200);
        (await context.Customers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(200);

        recorder.ExplicitFallbacks.ShouldBeEmpty();

        // Keys have to land on the entity whose row they belong to, not merely be distinct.
        var stored = await context.Customers.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Email, TestContext.Current.CancellationToken);

        customers.ShouldAllBe(c => stored[c.Id] == c.Email);
    }

    [Fact]
    public async Task An_insert_bringing_its_own_keys_is_still_accelerated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using var context = fixture.CreateBulkContext();

        var order = new Order { Customer = new Customer { Name = "c", Email = "c@example.com" } };
        context.Orders.Add(order);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        using var recorder = new PartitionRecorder();

        var notes = Enumerable.Range(0, 200).Select(i => new OrderNote
        {
            Id = new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
            OrderId = order.Id,
            Text = $"note {i}",
        }).ToList();

        await context.BulkInsertAsync(notes, cancellationToken: TestContext.Current.CancellationToken);

        recorder.ExplicitFallbacks.ShouldBeEmpty();
        (await context.OrderNotes.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(200);
    }
}
