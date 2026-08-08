using EFBulk.Equivalence.Infrastructure;
using EFBulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFBulk.Equivalence;

/// <summary>
///     Behaviour of the explicit <c>BulkInsertAsync</c> API.
/// </summary>
/// <remarks>
///     These are not differential tests. The explicit API deliberately does <em>not</em> behave like
///     <c>SaveChanges()</c> — it leaves detached input detached — so the contract is stated here
///     directly rather than compared against stock EF.
/// </remarks>
public abstract class BulkInsertApiTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Writes_rows_and_populates_generated_keys()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(500);
        var result = await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(500);
        result.Total.ShouldBe(500);

        // Keys land on the caller's objects regardless of tracking — that is the whole point of
        // separating the two.
        customers.ShouldAllBe(c => c.Id > 0);
        customers.Select(c => c.Id).Distinct().Count().ShouldBe(500);

        var stored = await context.Customers.AsNoTracking()
            .CountAsync(TestContext.Current.CancellationToken);
        stored.ShouldBe(500);
    }

    [Fact]
    public async Task Leaves_detached_input_detached_by_default()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(200);
        await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Entry(customers[0]).State.ShouldBe(EntityState.Detached);
    }

    [Fact]
    public async Task Track_attaches_entities_as_unchanged()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(200);
        await context.BulkInsertAsync(
            customers, o => o.Track(), TestContext.Current.CancellationToken);

        context.ChangeTracker.Entries<Customer>().Count().ShouldBe(200);
        context.Entry(customers[0]).State.ShouldBe(EntityState.Unchanged);

        // Unchanged means exactly that: a follow-up save must be a no-op, not 200 more inserts.
        (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task Already_tracked_entities_cannot_be_inserted_twice()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(150);
        context.Customers.AddRange(customers);   // tracked as Added

        await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        // Left as Added, the next SaveChanges would insert every row a second time and violate the
        // unique index. This is the one genuinely dangerous outcome the API has to rule out.
        context.Entry(customers[0]).State.ShouldBe(EntityState.Unchanged);
        (await context.SaveChangesAsync(TestContext.Current.CancellationToken)).ShouldBe(0);

        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(150);
    }

    [Fact]
    public async Task Respects_batch_size_and_reports_progress()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var progress = new List<BulkProgressSnapshot>();
        var customers = Customers(250);

        await context.BulkInsertAsync(
            customers,
            o => o.BatchSize(100).OnProgress(p => progress.Add(new(p.Completed, p.Total))),
            TestContext.Current.CancellationToken);

        progress.Count.ShouldBe(3);                       // 100 + 100 + 50
        progress[^1].Completed.ShouldBe(250);
        progress.ShouldAllBe(p => p.Total == 250);

        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(250);
    }

    [Fact]
    public async Task Writes_client_generated_keys_unchanged()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customer = Customers(1)[0];
        var order = new Order { Reference = "R1", Status = OrderStatus.Placed, PlacedAt = Epoch };
        customer.Orders.Add(order);
        context.Customers.Add(customer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var notes = Enumerable.Range(0, 300).Select(i => new OrderNote
        {
            Id = new Guid(i, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
            OrderId = order.Id,
            Text = $"Note {i}"
        }).ToList();

        await context.BulkInsertAsync(
            notes, cancellationToken: TestContext.Current.CancellationToken);

        var stored = await context.OrderNotes.AsNoTracking()
            .OrderBy(n => n.Text.Length).ThenBy(n => n.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(300);
        stored.Select(n => n.Id).ShouldBe(notes.Select(n => n.Id), ignoreOrder: true);
    }

    [Fact]
    public async Task Round_trips_value_converted_columns()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customer = Customers(1)[0];
        context.Customers.Add(customer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var statuses = Enum.GetValues<OrderStatus>();
        var orders = Enumerable.Range(0, 200).Select(i => new Order
        {
            CustomerId = customer.Id,
            Reference = $"REF-{i}",
            Status = statuses[i % statuses.Length],
            PlacedAt = Epoch.AddMinutes(i)
        }).ToList();

        await context.BulkInsertAsync(
            orders, cancellationToken: TestContext.Current.CancellationToken);

        var stored = await context.Orders.AsNoTracking()
            .OrderBy(o => o.Reference)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(200);
        foreach (var order in stored)
        {
            var index = int.Parse(order.Reference["REF-".Length..], provider: null);
            order.Status.ShouldBe(statuses[index % statuses.Length]);
        }
    }

    [Fact]
    public async Task Inserting_nothing_is_a_no_op()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var result = await context.BulkInsertAsync(
            Array.Empty<Customer>(), cancellationToken: TestContext.Current.CancellationToken);

        result.Total.ShouldBe(0);
    }

    [Fact]
    public async Task Surfaces_constraint_violations()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(150);
        customers[^1].Email = customers[0].Email;   // violates the unique index

        await Should.ThrowAsync<Exception>(
            () => context.BulkInsertAsync(
                customers, cancellationToken: TestContext.Current.CancellationToken));

        // The operation runs in one transaction, so a failure must leave nothing behind.
        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Bulk_update_writes_every_non_key_column()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(300);
        await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        foreach (var customer in customers)
        {
            customer.Name += " (updated)";
        }

        var result = await context.BulkUpdateAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        result.Updated.ShouldBe(300);

        var stored = await context.Customers.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(300);
        stored.ShouldAllBe(c => c.Name.EndsWith(" (updated)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bulk_delete_removes_rows_and_detaches()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(300);
        await context.BulkInsertAsync(
            customers, o => o.Track(), TestContext.Current.CancellationToken);

        var doomed = customers.Take(200).ToList();
        var result = await context.BulkDeleteAsync(
            doomed, cancellationToken: TestContext.Current.CancellationToken);

        result.Deleted.ShouldBe(200);

        // Their rows are gone, so leaving them tracked as Unchanged would assert a row that does
        // not exist and produce a phantom UPDATE on the next save.
        context.Entry(doomed[0]).State.ShouldBe(EntityState.Detached);

        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(100);
    }

    [Fact]
    public async Task Updating_a_missing_row_raises_a_concurrency_conflict()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(150);
        await context.BulkInsertAsync(
            customers, cancellationToken: TestContext.Current.CancellationToken);

        await context.BulkDeleteAsync(
            customers.Take(10).ToList(),
            cancellationToken: TestContext.Current.CancellationToken);

        foreach (var customer in customers)
        {
            customer.Name += " (updated)";
        }

        // A bulk statement reports one affected-row count for the whole set; recovering which rows
        // went missing is what makes this reportable at all.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => context.BulkUpdateAsync(
                customers, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Merge_on_an_alternate_key_inserts_and_updates()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var existing = Customers(100);
        await context.BulkInsertAsync(
            existing, cancellationToken: TestContext.Current.CancellationToken);

        // Half already exist (same emails, changed names), half are new.
        var incoming = Customers(100);
        foreach (var customer in incoming)
        {
            customer.Name += " (merged)";
        }

        incoming.AddRange(Customers(60, startAt: 500));

        var result = await context.BulkMergeAsync(
            incoming, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(60);
        result.Updated.ShouldBe(100);
        result.Total.ShouldBe(160);

        var stored = await context.Customers.AsNoTracking()
            .OrderBy(c => c.Email)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(160);
        stored.Count(c => c.Name.EndsWith(" (merged)", StringComparison.Ordinal)).ShouldBe(100);
    }

    [Fact]
    public async Task Merge_populates_generated_keys_on_newly_inserted_rows()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var incoming = Customers(80);
        var result = await context.BulkMergeAsync(
            incoming, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(80);
        result.Updated.ShouldBe(0);

        // Every row was an insert, so every entity should have come back with its key.
        incoming.ShouldAllBe(c => c.Id > 0);
        incoming.Select(c => c.Id).Distinct().Count().ShouldBe(80);
    }

    [Fact]
    public async Task Merge_is_idempotent()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var incoming = Customers(50);

        var first = await context.BulkMergeAsync(
            incoming, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);
        var second = await context.BulkMergeAsync(
            Customers(50), o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        first.Inserted.ShouldBe(50);
        second.Inserted.ShouldBe(0);
        second.Updated.ShouldBe(50);

        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(50);
    }

    [Fact]
    public async Task Synchronize_makes_the_table_match_the_source()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        await context.BulkInsertAsync(
            Customers(100), cancellationToken: TestContext.Current.CancellationToken);

        // Keep 60 of the originals (renamed), drop the other 40, add 25 new ones.
        var desired = Customers(60);
        foreach (var customer in desired)
        {
            customer.Name += " (synced)";
        }

        desired.AddRange(Customers(25, startAt: 900));

        var result = await context.BulkSynchronizeAsync(
            desired, o => o.MatchOn(c => c.Email), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(25);
        result.Updated.ShouldBe(60);
        result.Deleted.ShouldBe(40);

        var stored = await context.Customers.AsNoTracking()
            .OrderBy(c => c.Email)
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(85);
        stored.Count(c => c.Name.EndsWith(" (synced)", StringComparison.Ordinal)).ShouldBe(60);
    }

    [Fact]
    public async Task Synchronize_refuses_an_empty_source()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        await context.BulkInsertAsync(
            Customers(20), cancellationToken: TestContext.Current.CancellationToken);

        // Synchronising to nothing would empty the table. Far more often a bug in the caller's
        // query than an intent to truncate, so it is refused rather than obeyed.
        await Should.ThrowAsync<BulkNotSupportedException>(
            () => context.BulkSynchronizeAsync(
                Array.Empty<Customer>(),
                cancellationToken: TestContext.Current.CancellationToken));

        (await context.Customers.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(20);
    }

    [Fact]
    public async Task Bulk_save_changes_writes_the_tracked_graph()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(200);
        foreach (var customer in customers)
        {
            customer.Orders.Add(new Order
            {
                Reference = customer.Email,
                Status = OrderStatus.Placed,
                PlacedAt = Epoch
            });
        }

        context.Customers.AddRange(customers);
        var written = await context.BulkSaveChangesAsync(
            cancellationToken: TestContext.Current.CancellationToken);

        written.ShouldBe(400);

        // Being a real save, it still orders dependents after principals and populates keys.
        customers.ShouldAllBe(c => c.Id > 0);
        customers.ShouldAllBe(c => c.Orders[0].CustomerId == c.Id);

        (await context.Orders.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(200);
    }

    [Fact]
    public async Task Include_graph_writes_a_three_level_graph_in_dependency_order()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customers = Customers(100);
        foreach (var customer in customers)
        {
            var order = new Order
            {
                Customer = customer,
                Reference = customer.Email,
                Status = OrderStatus.Placed,
                PlacedAt = Epoch
            };

            for (var line = 1; line <= 3; line++)
            {
                order.Lines.Add(new OrderLine
                {
                    Order = order,
                    Sku = $"SKU-{line}",
                    Quantity = line,
                    UnitPrice = 9.99m * line
                });
            }

            customer.Orders.Add(order);
        }

        // Only Customers are passed in; Orders and OrderLines are reached through navigations.
        var result = await context.BulkInsertAsync(
            customers, o => o.IncludeGraph(), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(100 + 100 + 300);

        // Nothing set CustomerId or OrderId by hand — the foreign keys were filled in from the
        // navigations as each principal was written, which is what change tracking normally does.
        customers.ShouldAllBe(c => c.Id > 0);
        customers.ShouldAllBe(c => c.Orders[0].CustomerId == c.Id);
        customers.ShouldAllBe(c => c.Orders[0].Lines[0].OrderId == c.Orders[0].Id);

        (await context.OrderLines.AsNoTracking().CountAsync(TestContext.Current.CancellationToken))
            .ShouldBe(300);
    }

    [Fact]
    public async Task Include_graph_orders_a_self_referencing_tree_by_depth()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var roots = new List<Category>();
        for (var r = 0; r < 15; r++)
        {
            var root = new Category { Name = $"Root {r}" };
            for (var c = 0; c < 4; c++)
            {
                var mid = new Category { Name = $"Mid {r}.{c}", Parent = root };
                mid.Children.Add(new Category { Name = $"Leaf {r}.{c}", Parent = mid });
                root.Children.Add(mid);
            }

            roots.Add(root);
        }

        // One table whose rows depend on each other: table-level ordering cannot resolve this, so
        // the rows have to be layered by depth.
        var result = await context.BulkInsertAsync(
            roots, o => o.IncludeGraph(), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(15 + 60 + 60);

        var stored = await context.Categories.AsNoTracking()
            .ToListAsync(TestContext.Current.CancellationToken);

        stored.Count.ShouldBe(135);
        stored.Count(c => c.ParentId is null).ShouldBe(15);

        // Every non-root points at a row that actually exists.
        var ids = stored.Select(c => c.Id).ToHashSet();
        stored.Where(c => c.ParentId is not null)
            .ShouldAllBe(c => ids.Contains(c.ParentId!.Value));
    }

    [Fact]
    public async Task Include_graph_visits_each_entity_once()
    {
        await ResetAsync();
        await using var context = fixture.CreateBulkContext();

        var customer = Customers(1)[0];
        var order = new Order
        {
            Customer = customer,
            Reference = "R1",
            Status = OrderStatus.Placed,
            PlacedAt = Epoch
        };

        // Both directions of the same relationship, so a naive walk would revisit forever.
        customer.Orders.Add(order);

        var result = await context.BulkInsertAsync(
            new[] { customer }, o => o.IncludeGraph(), TestContext.Current.CancellationToken);

        result.Inserted.ShouldBe(2);
    }

    private readonly record struct BulkProgressSnapshot(int Completed, int Total);

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
}
