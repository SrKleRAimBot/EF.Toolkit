using EFToolkit.Bulk.Equivalence.Infrastructure;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence;

/// <summary>
///     <c>UpdateWhere</c> and <c>DeleteWhere</c> against EF Core's own <c>ExecuteUpdate</c> and
///     <c>ExecuteDelete</c>, written out by hand, on every engine.
/// </summary>
/// <remarks>
///     The object initializer is translated into <c>SetProperty</c> calls, so the reference here is
///     the same update with those calls spelled out. Each scenario runs both ways from the same seed
///     and requires the resulting rows to be identical — which is what catches an assignment the
///     translation dropped, a nested column it misaddressed, or a value converter it bypassed.
/// </remarks>
public abstract class SetBasedWriteTests(DatabaseFixture fixture)
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_update_reading_the_row_matches_the_hand_written_setters()
    {
        var suffix = "-renamed";

        var ours = await Run(async db => await db.Customers.UpdateWhereAsync(
            c => c.Id % 2 == 0,
            c => new Customer { Name = c.Name + suffix, CreatedAt = Epoch.AddDays(1) },
            Ct));

        var stock = await Run(async db => await db.Customers.Where(c => c.Id % 2 == 0).ExecuteUpdateAsync(
            s => s.SetProperty(c => c.Name, c => c.Name + suffix).SetProperty(c => c.CreatedAt, Epoch.AddDays(1)),
            Ct));

        ours.Affected.ShouldBe(stock.Affected);
        ours.Affected.ShouldBeGreaterThan(0);
        ours.Rows.ShouldBe(stock.Rows);
    }

    [Fact]
    public async Task An_update_through_a_value_converter_matches()
    {
        var ours = await Run(async db => await db.Orders.UpdateWhereAsync(
            o => o.Status == OrderStatus.Placed,
            o => new Order { Status = OrderStatus.Cancelled, Reference = o.Reference + "!" },
            Ct));

        var stock = await Run(async db => await db.Orders.Where(o => o.Status == OrderStatus.Placed).ExecuteUpdateAsync(
            s => s.SetProperty(o => o.Status, OrderStatus.Cancelled).SetProperty(o => o.Reference, o => o.Reference + "!"),
            Ct));

        ours.Affected.ShouldBe(stock.Affected);
        ours.Affected.ShouldBeGreaterThan(0);
        ours.Rows.ShouldBe(stock.Rows);
    }

    [Fact]
    public async Task A_nested_complex_initializer_matches()
    {
        var ours = await Run(async db => await db.Invoices.UpdateWhereAsync(
            i => i.Total.Currency == "EUR",
            i => new Invoice { Total = { Currency = "USD", Stamp = { By = "converter" } } },
            Ct));

        var stock = await Run(async db => await db.Invoices.Where(i => i.Total.Currency == "EUR").ExecuteUpdateAsync(
            s => s.SetProperty(i => i.Total.Currency, "USD").SetProperty(i => i.Total.Stamp.By, "converter"),
            Ct));

        ours.Affected.ShouldBe(stock.Affected);
        ours.Affected.ShouldBeGreaterThan(0);
        ours.Rows.ShouldBe(stock.Rows);
    }

    [Fact]
    public async Task A_delete_matches()
    {
        var ours = await Run(async db => await db.OrderLines.DeleteWhereAsync(l => l.Quantity > 2, Ct));
        var stock = await Run(async db => await db.OrderLines.Where(l => l.Quantity > 2).ExecuteDeleteAsync(Ct));

        ours.Affected.ShouldBe(stock.Affected);
        ours.Affected.ShouldBeGreaterThan(0);
        ours.Rows.ShouldBe(stock.Rows);
    }

    // ---------------------------------------------------------------------------------------

    // Both runs use the bulk-configured database, one after the other from the same seed: what differs
    // between them is only how the statement was written, not which context wrote it.
    private async Task<(int Affected, List<string> Rows)> Run(Func<ShopContext, Task<int>> write)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using (var seed = fixture.CreateBulkContext())
        {
            for (var i = 0; i < 6; i++)
            {
                var customer = new Customer { Name = $"Customer {i}", Email = $"c{i}@example.com", CreatedAt = Epoch };
                var order = new Order
                {
                    Customer = customer,
                    Reference = $"R{i}",
                    Status = i % 2 == 0 ? OrderStatus.Placed : OrderStatus.Shipped,
                    PlacedAt = Epoch.AddHours(i),
                };

                for (var q = 1; q <= 4; q++)
                {
                    order.Lines.Add(new OrderLine { Sku = $"S{i}-{q}", Quantity = q, UnitPrice = q });
                }

                seed.Orders.Add(order);
                seed.Invoices.Add(new Invoice
                {
                    Reference = $"I{i}",
                    Total = new Money { Amount = 10 * i, Currency = i % 2 == 0 ? "EUR" : "GBP", Stamp = { By = "seed" } },
                });
            }

            await seed.SaveChangesAsync(Ct);
        }

        int affected;
        await using (var context = fixture.CreateBulkContext())
        {
            affected = await write(context);
        }

        await using var read = fixture.CreateBulkContext();
        var rows = new List<string>();
        rows.AddRange((await read.Customers.OrderBy(c => c.Id).ToListAsync(Ct))
            .Select(c => $"customer {c.Id} {c.Name} {c.Email} {c.CreatedAt:O}"));
        rows.AddRange((await read.Orders.OrderBy(o => o.Id).ToListAsync(Ct))
            .Select(o => $"order {o.Id} {o.Reference} {o.Status} {o.CustomerId}"));
        rows.AddRange((await read.OrderLines.OrderBy(l => l.Id).ToListAsync(Ct))
            .Select(l => $"line {l.Id} {l.Sku} {l.Quantity}"));
        rows.AddRange((await read.Invoices.OrderBy(i => i.Id).ToListAsync(Ct))
            .Select(i => $"invoice {i.Id} {i.Total.Amount} {i.Total.Currency} {i.Total.Stamp.By} {i.Discount?.Amount}"));
        return (affected, rows);
    }
}
