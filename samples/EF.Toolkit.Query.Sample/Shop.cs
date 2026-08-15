using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Sample;

public enum OrderStatus
{
    Placed = 0,
    Shipped = 1,
    Cancelled = 2,
}

public class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public List<Order> Orders { get; } = [];
}

public class Order
{
    public int Id { get; set; }

    public DateTime PlacedAt { get; set; }

    public decimal Total { get; set; }

    public OrderStatus Status { get; set; }

    public int CustomerId { get; set; }

    public string Reference { get; set; } = "";

    public Customer? Customer { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.Email).HasMaxLength(256);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.HasOne(x => x.Customer).WithMany(x => x.Orders).HasForeignKey(x => x.CustomerId);

            // Covers (PlacedAt, Id) — the ordering sections 2 and 3 page along. The advisor in
            // section 6 reports a query ordered by Total precisely because nothing covers that one.
            b.HasIndex(x => new { x.PlacedAt, x.Id });
        });
    }
}
