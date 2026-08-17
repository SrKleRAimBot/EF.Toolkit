using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence.Model;

/// <summary>The model the audit suite runs against.</summary>
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Assignment> Assignments => Set<Assignment>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.Property(p => p.Status).HasConversion<string>();
            b.Property(p => p.Price).HasPrecision(18, 2);
            b.HasIndex(p => p.Sku).IsUnique();

            b.ComplexProperty(p => p.Dimensions, d =>
            {
                d.Property(x => x.Width).HasPrecision(18, 2);
                d.Property(x => x.Height).HasPrecision(18, 2);
                d.ComplexProperty(x => x.Packaging)
                    .Property(x => x.Material).HasMaxLength(32).IsRequired();
            });

            b.IsAudited(a => a
                .Exclude(p => p.InternalNotes)
                .Mask(p => p.CardNumber));
        });

        modelBuilder.Entity<Warehouse>(b =>
        {
            b.Property(w => w.Id).ValueGeneratedNever();
            b.OwnsOne(w => w.Address);

            b.IsAudited();
        });

        modelBuilder.Entity<Assignment>(b =>
        {
            b.HasKey(a => new { a.ProductSku, a.UserId });
            b.IsAudited();
        });

        modelBuilder.Entity<Shift>(b =>
        {
            // Merge and synchronise match on the code, which needs a unique index behind it.
            b.HasIndex(s => s.Code).IsUnique();

            b.IsAudited();
        });

        // ApiKey is registered by [Audited]; Session is registered nowhere and must stay unaudited.
        modelBuilder.Entity<ApiKey>();
        modelBuilder.Entity<Session>();
    }
}
