using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Sql.Integration.Infrastructure;

/// <summary>The model every scenario runs against.</summary>
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<BigOrder> BigOrders => Set<BigOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().Property(o => o.Total).HasPrecision(10, 2);
        modelBuilder.Entity<BigOrder>().HasNoKey().ToView("big_orders");
    }
}

public sealed class Order
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public decimal Total { get; set; }

    public string? Note { get; set; }
}

/// <summary>The table row-level security is tested on, kept apart so it cannot hide rows from other suites.</summary>
public sealed class Document
{
    public int Id { get; set; }

    public int TenantId { get; set; }

    public string Title { get; set; } = "";
}

/// <summary>A keyless entity over a view the tests create.</summary>
public sealed class BigOrder
{
    public int Id { get; set; }

    public decimal Total { get; set; }
}

/// <summary>An unmapped row type for function and procedure results.</summary>
public sealed class OrderRow
{
    public int Id { get; set; }

    public decimal Total { get; set; }
}
