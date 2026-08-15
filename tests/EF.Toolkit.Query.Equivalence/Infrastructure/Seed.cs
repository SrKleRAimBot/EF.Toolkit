using EFToolkit.Query.Equivalence.Model;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence.Infrastructure;

/// <summary>Deterministic seed data, shaped so that page boundaries land inside ties.</summary>
/// <remarks>
///     The duplication is the point. Over distinct values a keyset predicate written as
///     <c>a &gt; a0 &amp;&amp; b &gt; b0</c> is indistinguishable from a correct one, and only starts
///     losing rows when several share the leading column. Every count here is chosen so that at least
///     one page boundary falls between two rows that tie.
/// </remarks>
public static class Seed
{
    /// <summary>The fixed instant every seeded date is measured from.</summary>
    /// <remarks>
    ///     UTC rather than unspecified because Npgsql maps <see cref="DateTime" /> to
    ///     <c>timestamp with time zone</c> and refuses to write any other kind to it. SQL Server's
    ///     <c>datetime2</c> ignores the kind, so UTC is the one choice both engines accept.
    /// </remarks>
    public static DateTime Epoch { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Writes <paramref name="orderCount" /> orders across four customers.</summary>
    /// <returns>The orders as written, in insertion order.</returns>
    public static async Task<IReadOnlyList<Order>> OrdersAsync(
        ShopContext context,
        int orderCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var customers = Enumerable.Range(1, 4)
            .Select(i => new Customer { Name = $"Customer {i:D2}", Email = $"c{i}@example.com" })
            .ToArray();

        context.Customers.AddRange(customers);
        await context.SaveChangesAsync(cancellationToken);

        var orders = new List<Order>(orderCount);

        for (var i = 0; i < orderCount; i++)
        {
            orders.Add(new Order
            {
                // Five orders per date, so a page size of 3, 4 or 7 always cuts through a group of
                // rows sharing PlacedAt.
                PlacedAt = Epoch.AddDays(i / 5),

                // Totals repeat on a four-cycle against a status on a three-cycle, so the middle
                // component of a three-part ordering ties heavily without the two ever lining up. A
                // correlated seed would make "expensive and not cancelled" match nothing, and a test
                // asserting on that filter would pass for the wrong reason.
                Total = new[] { 10.25m, 99.99m, 55.50m, 10.25m }[i % 4],

                Status = (OrderStatus)(i % 3),
                CustomerId = customers[i % customers.Length].Id,
                Reference = $"REF-{i % 7:D2}",
                Note = i % 4 == 0 ? null : $"note {i}",
            });
        }

        context.Orders.AddRange(orders);
        await context.SaveChangesAsync(cancellationToken);

        return orders;
    }

    /// <summary>Writes <paramref name="count" /> shipments with client-generated Guid keys.</summary>
    /// <returns>The shipments as written.</returns>
    public static async Task<IReadOnlyList<Shipment>> ShipmentsAsync(
        ShopContext context,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var shipments = Enumerable.Range(0, count)
            .Select(i => new Shipment
            {
                // Version-4 Guids rather than sequential ones: the whole point is that the database's
                // ordering of them is nothing like insertion order, so a page walked by Guid has to
                // agree with the engine's own sort rather than with .NET's.
                Id = Guid.NewGuid(),
                DispatchedAt = new DateTimeOffset(Epoch.AddHours(i / 3), TimeSpan.Zero),
                Carrier = $"Carrier {i % 3}",
            })
            .ToArray();

        context.Shipments.AddRange(shipments);
        await context.SaveChangesAsync(cancellationToken);

        return shipments;
    }
}
