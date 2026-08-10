using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence.Model;

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<OrderNote> OrderNotes => Set<OrderNote>();
    public DbSet<Inventory> Inventories => Set<Inventory>();
    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();

            // Value conversion is where a bulk writer most easily diverges from EF: it writes the
            // copy stream directly rather than going through EF's parameter construction.
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);

            b.HasOne(x => x.Customer)
                .WithMany(x => x.Orders)
                .HasForeignKey(x => x.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.Category)
                .WithMany()
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<OrderLine>(b =>
        {
            b.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);

            b.HasOne(x => x.Order)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderNote>(b =>
        {
            // ValueGeneratedNever keeps key generation entirely client-side, so an insert has
            // nothing to read back and can take the no-correlation fast path.
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Text).HasMaxLength(500).IsRequired();

            b.HasOne(x => x.Order)
                .WithMany(x => x.Notes)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Inventory>(b =>
        {
            b.Property(x => x.Sku).HasMaxLength(64).IsRequired();

            // A client-managed token: EF puts it in the WHERE clause using the loaded value while
            // the SET clause assigns the new one, so the column is both a condition and written.
            b.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<Shipment>(b =>
        {
            // Store-generated *and* converted. The database hands back a plain int, so whatever
            // propagates it onto the entity has to run the converter in reverse — the direction
            // that is easy to forget, because writes look correct without it.
            b.Property(x => x.Id)
                .HasConversion(id => id.Value, value => new ShipmentId(value))
                .ValueGeneratedOnAdd();

            b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<Category>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();

            b.HasOne(x => x.Parent)
                .WithMany(x => x.Children)
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
