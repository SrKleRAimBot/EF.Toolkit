using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence.Model;

/// <summary>The test model. Every configuration here exists to catch a specific failure mode.</summary>
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
            b.Property(x => x.Email).HasMaxLength(256);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);

            // Four decimal places against values carrying two: a cursor that rendered the boundary
            // with the column's scale rather than the value's would compare unequal to the very row
            // it was built from, and the page after it would start in the wrong place.
            b.Property(x => x.Total).HasPrecision(18, 4);

            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();
            b.Property(x => x.Note).HasMaxLength(256);

            b.HasOne(x => x.Customer).WithMany(x => x.Orders).HasForeignKey(x => x.CustomerId);

            // The ordering the keyset suites walk along, so the engine can serve it from an index and
            // a plan regression shows up as a timeout rather than as a wrong answer.
            b.HasIndex(x => new { x.PlacedAt, x.Id });
        });

        modelBuilder.Entity<Shipment>(b =>
        {
            // Client-generated so the seeded values are known up front and the same Guids can be
            // asserted against; a store-generated key would order by whatever the server produced.
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Carrier).HasMaxLength(64).IsRequired();
        });
    }
}
