using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     Asserts that partitioning does real work against a live database.
/// </summary>
/// <remarks>
///     The equivalence suite proves EF.Toolkit.Bulk produces the same results as stock EF. On its own that
///     is also what a partitioner that never split anything would produce, so these tests check the
///     mechanism is actually engaging — and, crucially, that a batch really can contain more than
///     one shape, which is the case where regrouping reorders commands.
/// </remarks>
public abstract class PartitioningTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_batch_containing_several_shapes_is_split_and_still_correct()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();

        await using (var seed = fixture.CreateBulkContext())
        {
            seed.Customers.AddRange(Customers(40));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var context = fixture.CreateBulkContext())
        {
            var existing = await context.Customers.OrderBy(c => c.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            // Updates and deletes on one table plus inserts on another, with no dependencies
            // between them, so EF's topological sort can hand them over in a single command set.
            foreach (var customer in existing.Take(15))
            {
                customer.Name += " (updated)";
            }

            context.Customers.RemoveRange(existing.Skip(15).Take(10));
            context.Categories.AddRange(
                Enumerable.Range(0, 12).Select(i => new Category { Name = $"Category {i}" }));

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        recorder.Batches.ShouldNotBeEmpty("EF.Toolkit.Bulk planned no batches at all.");

        var multiShape = recorder.MultiPartitionBatches.ToList();
        multiShape.ShouldNotBeEmpty(
            "No batch was split into multiple partitions, so the reordering path this suite is "
            + "supposed to cover never executed. Partition shapes seen: "
            + string.Join(" / ", recorder.Batches.Select(
                b => string.Join(", ", b.Select(p => p.ToString())))));

        // Every command must survive regrouping.
        foreach (var batch in recorder.Batches)
        {
            var partitioned = batch.Sum(p => p.Commands.Count);
            var distinct = batch.SelectMany(p => p.Commands).Distinct().Count();
            distinct.ShouldBe(partitioned, "A command appeared in more than one partition.");
        }
    }

    [Fact]
    public async Task Partitions_report_why_they_did_not_accelerate()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();

        await using var context = fixture.CreateBulkContext();
        context.Customers.AddRange(Customers(5));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var executed = recorder.Executions.ShouldHaveSingleItem();
        executed.Partition.TableName.ShouldBe("Customers");

        // No executor is registered yet, so this must fall back — and say so. A silent fallback is
        // the failure mode that would make EF.Toolkit.Bulk look correct while delivering nothing.
        executed.Accelerated.ShouldBeFalse();
        executed.FallbackReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Client_generated_keys_actually_take_the_bulk_path()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        int orderId;
        await using (var seed = fixture.CreateBulkContext())
        {
            var customer = Customers(1)[0];
            var order = new Order { Reference = "R1", Status = OrderStatus.Placed, PlacedAt = Epoch };
            customer.Orders.Add(order);
            seed.Customers.Add(customer);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            orderId = order.Id;
        }

        using var recorder = new PartitionRecorder();

        await using (var context = fixture.CreateBulkContext())
        {
            context.OrderNotes.AddRange(Enumerable.Range(0, 300).Select(i => new OrderNote
            {
                Id = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
                OrderId = orderId,
                Text = $"Note {i}"
            }));

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var notes = recorder.Executions
            .Where(e => e.Partition.TableName == "OrderNotes")
            .ToList();

        notes.ShouldNotBeEmpty("No OrderNotes partition was executed at all.");

        // Passing the equivalence suite proves EF.Toolkit.Bulk is correct; it does not prove it is fast.
        // A COPY path that silently fell back would look identical from the outside, which is
        // exactly the failure this asserts against.
        notes.ShouldAllBe(e => e.Accelerated);
        notes.Sum(e => e.Partition.Commands.Count).ShouldBe(300);
    }

    [Fact]
    // PostgreSQL reserves sequence values up front; SQL Server has no sequence behind an
    // IDENTITY column and instead stages the rows and correlates via MERGE ... OUTPUT. Both
    // must end with the keys on the entities, which is what this asserts.
    public async Task Server_generated_keys_are_accelerated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var recorder = new PartitionRecorder();

        var customers = Customers(250);
        await using (var context = fixture.CreateBulkContext())
        {
            context.Customers.AddRange(customers);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var inserts = recorder.Executions
            .Where(e => e.Partition.TableName == "Customers")
            .ToList();

        inserts.ShouldNotBeEmpty();
        inserts.ShouldAllBe(
            e => e.Accelerated,
            "Identity-keyed inserts were not accelerated: "
            + string.Join("; ", inserts.Select(e => e.FallbackReason)));

        // Reserving keys is only worth anything if the values actually reach the entities: this is
        // the whole "retains EF change tracking" claim, reduced to one assertion.
        customers.ShouldAllBe(c => c.Id > 0);
        customers.Select(c => c.Id).Distinct().Count().ShouldBe(customers.Count);
    }

    [Fact]
    public async Task Updates_and_deletes_are_accelerated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            seed.Customers.AddRange(Customers(400));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var recorder = new PartitionRecorder();

        await using (var context = fixture.CreateBulkContext())
        {
            var existing = await context.Customers.OrderBy(c => c.Id)
                .ToListAsync(TestContext.Current.CancellationToken);

            foreach (var customer in existing.Take(200))
            {
                customer.Name += " (updated)";
            }

            context.Customers.RemoveRange(existing.Skip(200).Take(150));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var updates = recorder.Executions
            .Where(e => e.Partition.EntityState == EntityState.Modified).ToList();
        var deletes = recorder.Executions
            .Where(e => e.Partition.EntityState == EntityState.Deleted).ToList();

        updates.ShouldNotBeEmpty();
        deletes.ShouldNotBeEmpty();

        updates.ShouldAllBe(
            e => e.Accelerated,
            "Updates fell back: " + string.Join("; ", updates.Select(e => e.FallbackReason)));
        deletes.ShouldAllBe(
            e => e.Accelerated,
            "Deletes fell back: " + string.Join("; ", deletes.Select(e => e.FallbackReason)));
    }

    [Fact]
    public async Task Concurrency_token_updates_are_accelerated()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            seed.Inventories.AddRange(Enumerable.Range(0, 300).Select(i => new Inventory
            {
                Sku = $"SKU-{i:D5}",
                Quantity = i,
                Version = 1
            }));

            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var recorder = new PartitionRecorder();

        await using (var context = fixture.CreateBulkContext())
        {
            foreach (var item in await context.Inventories
                .ToListAsync(TestContext.Current.CancellationToken))
            {
                item.Quantity += 5;
                item.Version++;
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var updates = recorder.Executions
            .Where(e => e.Partition.EntityState == EntityState.Modified)
            .ToList();

        updates.ShouldNotBeEmpty();

        // A token column is written and used to locate the row at the same time. Before the
        // staging layout carried both values it was declined outright, and the equivalence suite
        // could not tell the difference because falling back is also correct.
        updates.ShouldAllBe(
            e => e.Accelerated,
            "Concurrency-token updates fell back: "
            + string.Join("; ", updates.Select(e => e.FallbackReason)));
    }

    [Fact]
    public async Task An_outer_recorder_resumes_when_an_inner_one_is_disposed()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        using var outer = new PartitionRecorder();

        using (var inner = new PartitionRecorder())
        {
            await using var seed = fixture.CreateBulkContext();
            seed.Customers.AddRange(Customers(5));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);

            inner.Batches.ShouldNotBeEmpty("The innermost recorder did not capture its own work.");
            outer.Batches.ShouldBeEmpty("Work inside a nested scope belongs to the inner recorder.");
        }

        await using (var context = fixture.CreateBulkContext())
        {
            context.Categories.AddRange(
                Enumerable.Range(0, 5).Select(i => new Category { Name = $"Category {i}" }));

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // A recorder that stopped seeing events halfway through would not fail loudly: the
        // assertions it feeds are all "did this happen", so it would simply report less work than
        // was done and any test relying on it would start passing for the wrong reason.
        outer.Batches.ShouldNotBeEmpty("Disposing the nested recorder silenced the outer one.");
    }

    private static List<Customer> Customers(int count)
        =>
        [
            .. Enumerable.Range(0, count).Select(i => new Customer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                CreatedAt = Epoch.AddMinutes(i)
            })
        ];
}
