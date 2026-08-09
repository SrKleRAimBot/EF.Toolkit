using Microsoft.EntityFrameworkCore;

namespace EFBulk.Sample;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public decimal Balance { get; set; }

    public List<Order> Orders { get; } = [];
}

public class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public string Reference { get; set; } = "";
    public decimal Total { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.Balance).HasPrecision(18, 2);

            // BulkMergeAsync matches on this, and ON CONFLICT needs a unique index to define what
            // a conflict is.
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();
            b.Property(x => x.Total).HasPrecision(18, 2);

            b.HasOne(x => x.Customer)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
