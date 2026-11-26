using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests.Infrastructure;

/// <summary>Hand-built rows, for the tests that are not about the builder.</summary>
public static class ShopData
{
    private static int _next;

    public static Customer Customer(string? name = null)
    {
        var n = Interlocked.Increment(ref _next);
        return new Customer
        {
            Name = name ?? $"C{n}",
            Email = $"c{n}@example.com",
            Tier = Tier.Silver,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ExternalId = Guid.NewGuid(),
            Address = new Address { Street = "1 Main St", City = "Wellington" },
        };
    }

    public static Product Product()
    {
        var n = Interlocked.Increment(ref _next);
        return new Product
        {
            Sku = $"S{n % 1000}",
            Name = $"Product {n}",
            Thumbprint = [1, 2],
            Tags = ["a"],
        };
    }

    public static Order Order(Customer customer, params Product[] products)
    {
        var order = new Order
        {
            Customer = customer,
            Total = 10m,
            PlacedOn = new DateOnly(2026, 1, 1),
            Shipping = new Shipping { Carrier = "Post" },
        };

        foreach (var product in products)
        {
            order.Lines.Add(new OrderLine { Product = product, Quantity = 1 });
        }

        return order;
    }

    /// <summary>A customer with an order of two lines, plus a category tree and a team with its captain.</summary>
    public static async Task SeedEverythingAsync(ShopContext context)
    {
        var customer = Customer();
        context.Add(Order(customer, Product(), Product()));

        var root = new Category { Name = "Root" };
        context.Add(new Category { Name = "Child", Parent = new Category { Name = "Middle", Parent = root } });

        context.Add(new Tag { Code = "t1", Label = "Tag" });

        // A cycle can only be inserted in two steps: the team first, then the captain who points at it.
        var team = new Team { Name = "Team" };
        var captain = new Player { Name = "Captain", Team = team };
        context.Add(captain);
        await context.SaveChangesAsync();

        team.Captain = captain;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    public static async Task<Dictionary<string, int>> CountsAsync(ShopContext context)
        => new()
        {
            ["Customers"] = await context.Customers.CountAsync(),
            ["Orders"] = await context.Orders.CountAsync(),
            ["OrderLines"] = await context.OrderLines.CountAsync(),
            ["Products"] = await context.Products.CountAsync(),
            ["Categories"] = await context.Categories.CountAsync(),
            ["Teams"] = await context.Teams.CountAsync(),
            ["Players"] = await context.Players.CountAsync(),
            ["Tags"] = await context.Tags.CountAsync(),
        };
}
