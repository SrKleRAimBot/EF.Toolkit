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
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<Invoice> Invoices => Set<Invoice>();

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

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.Property(x => x.Action).HasMaxLength(64).IsRequired();

            // A database default on a non-key column. EF omits the column from the insert whenever
            // the property holds the CLR default and reads the generated value back instead.
            b.Property(x => x.Severity).HasDefaultValue(3);
        });

        modelBuilder.Entity<Sensor>(b => b.Property(x => x.Name).HasMaxLength(64).IsRequired());

        modelBuilder.Entity<Reading>(b =>
        {
            // A CHECK constraint and a foreign key, both of which SqlBulkCopy skips unless asked.
            b.ToTable(t => t.HasCheckConstraint("CK_Reading_Value", "\"Value\" >= 0"));

            b.HasOne(x => x.Sensor)
                .WithMany()
                .HasForeignKey(x => x.SensorId)
                .OnDelete(DeleteBehavior.Cascade);

            // A default on a nullable column: without KeepNulls the server substitutes this
            // wherever a null is written, so an explicit null would silently become 'unlabelled'.
            b.Property(x => x.Label).HasMaxLength(64).HasDefaultValue("unlabelled");
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();

            // Complex properties, which map to columns of this table that the entity does not map
            // itself. The nested one gives Total_Stamp_By, and the optional one has to come back as
            // nulls when it was never set.
            b.ComplexProperty(x => x.Total, t =>
            {
                t.Property(m => m.Amount).HasPrecision(18, 2);
                t.Property(m => m.Currency).HasMaxLength(3).IsRequired();
                t.ComplexProperty(m => m.Stamp).Property(s => s.By).HasMaxLength(64).IsRequired();
            });

            b.ComplexProperty(x => x.Discount, d =>
            {
                d.Property(m => m.Amount).HasPrecision(18, 2);
                d.Property(m => m.Currency).HasMaxLength(3);
                d.ComplexProperty(m => m.Stamp).Property(s => s.By).HasMaxLength(64);
            });
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
