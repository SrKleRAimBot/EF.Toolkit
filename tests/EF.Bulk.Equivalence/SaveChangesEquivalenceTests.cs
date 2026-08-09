using EFBulk.Equivalence.Infrastructure;
using EFBulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFBulk.Equivalence;

/// <summary>
///     Scenarios asserting that <c>SaveChanges()</c> under EF.Bulk is indistinguishable from
///     <c>SaveChanges()</c> under stock EF Core.
/// </summary>
/// <remarks>
///     Written once here and executed against every engine by the thin subclasses in
///     <c>Engines/</c>, so a scenario cannot accidentally cover only one database.
/// </remarks>
public abstract class SaveChangesEquivalenceTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private Task AssertEquivalent(
        Func<ShopContext, Task> scenario,
        string? failureMessagesDifferBecause = null)
        => Differential.AssertAsync(fixture, scenario, failureMessagesDifferBecause);

    [Fact]
    public Task Insert_single_table_above_threshold()
        => AssertEquivalent(async context =>
        {
            context.Customers.AddRange(Customers(500));
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_single_table_below_threshold()
        => AssertEquivalent(async context =>
        {
            // Under the bulk threshold, so this must run through stock EF on both sides and still
            // produce identical results — the fallback path is as much a correctness surface as
            // the fast one.
            context.Customers.AddRange(Customers(3));
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_parent_and_children_in_one_save()
        => AssertEquivalent(async context =>
        {
            foreach (var customer in Customers(200))
            {
                customer.Orders.Add(NewOrder(customer, 1));
                customer.Orders.Add(NewOrder(customer, 2));
                context.Customers.Add(customer);
            }

            // Orders cannot be written before their Customer's key exists, so this is the case
            // that a naive "group by type and insert" would get wrong.
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_three_level_graph_in_one_save()
        => AssertEquivalent(async context =>
        {
            foreach (var customer in Customers(100))
            {
                var order = NewOrder(customer, 1);
                for (var line = 1; line <= 3; line++)
                {
                    order.Lines.Add(new OrderLine
                    {
                        Sku = $"SKU-{line}",
                        Quantity = line,
                        UnitPrice = 9.99m * line
                    });
                }

                customer.Orders.Add(order);
                context.Customers.Add(customer);
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_self_referencing_tree_in_one_save()
        => AssertEquivalent(async context =>
        {
            // A single table whose rows depend on each other: table-level ordering cannot resolve
            // this, so it needs row-level layering.
            for (var root = 0; root < 20; root++)
            {
                var parent = new Category { Name = $"Root {root}" };
                for (var child = 0; child < 5; child++)
                {
                    var mid = new Category { Name = $"Mid {root}.{child}" };
                    mid.Children.Add(new Category { Name = $"Leaf {root}.{child}" });
                    parent.Children.Add(mid);
                }

                context.Categories.Add(parent);
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_with_nullable_foreign_key_both_set_and_null()
        => AssertEquivalent(async context =>
        {
            var category = new Category { Name = "Retail" };
            context.Categories.Add(category);

            var customers = Customers(150);
            for (var i = 0; i < customers.Count; i++)
            {
                var order = NewOrder(customers[i], 1);
                order.Category = i % 2 == 0 ? category : null;
                customers[i].Orders.Add(order);
            }

            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Update_after_insert()
        => AssertEquivalent(async context =>
        {
            context.Customers.AddRange(Customers(200));
            await context.SaveChangesAsync();

            foreach (var customer in await context.Customers.OrderBy(c => c.Id).Take(150).ToListAsync())
            {
                customer.Name += " (updated)";
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Delete_after_insert()
        => AssertEquivalent(async context =>
        {
            context.Customers.AddRange(Customers(200));
            await context.SaveChangesAsync();

            var doomed = await context.Customers.OrderBy(c => c.Id).Take(120).ToListAsync();
            context.Customers.RemoveRange(doomed);

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Mixed_insert_update_and_delete_in_one_save()
        => AssertEquivalent(async context =>
        {
            context.Customers.AddRange(Customers(300));
            await context.SaveChangesAsync();

            var existing = await context.Customers.OrderBy(c => c.Id).ToListAsync();

            foreach (var customer in existing.Take(100))
            {
                customer.Name += " (updated)";
            }

            context.Customers.RemoveRange(existing.Skip(100).Take(100));
            context.Customers.AddRange(Customers(150, startAt: 1000));

            // All three states in one save: EF hands these over as separate command sets, and the
            // relative order between them has to survive partitioning.
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Value_converted_enum_round_trips()
        => AssertEquivalent(async context =>
        {
            var statuses = Enum.GetValues<OrderStatus>();
            var customers = Customers(120);

            for (var i = 0; i < customers.Count; i++)
            {
                var order = NewOrder(customers[i], 1);
                order.Status = statuses[i % statuses.Length];
                customers[i].Orders.Add(order);
            }

            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_client_generated_keys_needing_no_readback()
        => AssertEquivalent(async context =>
        {
            var customer = Customers(1)[0];
            var order = NewOrder(customer, 1);
            customer.Orders.Add(order);
            context.Customers.Add(customer);
            await context.SaveChangesAsync();

            // Guid keys are assigned client-side, so this insert has nothing to read back and
            // reaches the fastest path: a straight bulk copy with no staging and no correlation.
            for (var i = 0; i < 400; i++)
            {
                order.Notes.Add(new OrderNote { Id = NoteId(i), Text = $"Note {i}" });
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Insert_client_generated_keys_alongside_generated_ones()
        => AssertEquivalent(async context =>
        {
            // Notes depend on Orders whose keys are server-generated, so a single save must order
            // the two: the accelerated child insert cannot run until the parent's keys exist.
            var noteSeed = 0;
            foreach (var customer in Customers(60))
            {
                var order = NewOrder(customer, 1);
                for (var i = 0; i < 5; i++)
                {
                    order.Notes.Add(new OrderNote { Id = NoteId(noteSeed++), Text = $"Note {i}" });
                }

                customer.Orders.Add(order);
                context.Customers.Add(customer);
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Update_with_a_concurrency_token()
        => AssertEquivalent(async context =>
        {
            context.Inventories.AddRange(Inventories(200));
            await context.SaveChangesAsync();

            // The token is both written and used to locate the row, so a staged update has to
            // carry its loaded value and its new value at the same time.
            foreach (var item in await context.Inventories.OrderBy(i => i.Id).ToListAsync())
            {
                item.Quantity += 10;
                item.Version++;
            }

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Concurrency_conflict_fails_the_same_way()
        => AssertEquivalent(async context =>
        {
            context.Inventories.AddRange(Inventories(150));
            await context.SaveChangesAsync();

            var items = await context.Inventories.OrderBy(i => i.Id).ToListAsync();

            // Simulate someone else having written row 40 since it was loaded: its stored Version
            // no longer matches the one this context holds. ExecuteUpdate bypasses the change
            // tracker, so the loaded entity keeps its now-stale value -- exactly the situation
            // optimistic concurrency exists to catch.
            var staleId = items[40].Id;
            await context.Inventories
                .Where(i => i.Id == staleId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Version, i => i.Version + 1));

            foreach (var item in items)
            {
                item.Quantity += 1;
                item.Version++;
            }

            await context.SaveChangesAsync();
        },
        // The type, and the entries the exception carries, must match; the wording cannot. Stock
        // EF fails the one statement whose row vanished and counts in ones, while a bulk update is
        // a single statement that reports how many of the 150 rows it matched in total.
        failureMessagesDifferBecause:
            "a bulk statement reports one aggregate affected-row count where stock EF reports "
            + "a per-row one.");

    [Fact]
    public Task Delete_with_a_concurrency_token()
        => AssertEquivalent(async context =>
        {
            context.Inventories.AddRange(Inventories(200));
            await context.SaveChangesAsync();

            var items = await context.Inventories.OrderBy(i => i.Id).Take(120).ToListAsync();
            context.Inventories.RemoveRange(items);

            await context.SaveChangesAsync();
        });

    [Fact]
    public Task Unique_constraint_violation_fails_the_same_way()
        => AssertEquivalent(async context =>
        {
            var customers = Customers(150);
            // Email is unique: both sides must surface the same failure, and leave the same state
            // behind. A bulk path that reported success here would be far worse than a slow one.
            customers[^1].Email = customers[0].Email;

            context.Customers.AddRange(customers);
            await context.SaveChangesAsync();
        });

    /// <summary>
    ///     A deterministic Guid for <paramref name="seed" />.
    /// </summary>
    /// <remarks>
    ///     The harness runs each scenario twice and compares the results, so a scenario must be a
    ///     pure function of its inputs. <see cref="Guid.NewGuid" /> would produce different keys on
    ///     the two runs and the databases would diverge for reasons that have nothing to do with
    ///     EF.Bulk.
    /// </remarks>
    private static Guid NoteId(int seed)
        => new(seed, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static List<Inventory> Inventories(int count)
        =>
        [
            .. Enumerable.Range(0, count).Select(i => new Inventory
            {
                Sku = $"SKU-{i:D5}",
                Quantity = i,
                Version = 1
            })
        ];

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

    private static Order NewOrder(Customer customer, int index)
        => new()
        {
            Reference = $"{customer.Email}-{index}",
            Status = OrderStatus.Placed,
            PlacedAt = Epoch.AddHours(index)
        };
}
