using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Sample;

/// <summary>A product, audited with one column masked and one left out.</summary>
public class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }

    /// <summary>Read by <c>MultiTenant(t => t.FromEntityProperty())</c>.</summary>
    public string TenantId { get; set; } = "";

    /// <summary>Masked: the trail records that it changed, not what it changed to.</summary>
    public string? SupplierAccount { get; set; }

    /// <summary>Excluded: noise, and it leaves no trace at all.</summary>
    public string? ScratchNotes { get; set; }
}

/// <summary>Stored as text, so the payload records the text rather than an ordinal.</summary>
public enum ProductStatus
{
    Draft,
    Live,
    Retired,
}

/// <summary>Not registered, and so never audited.</summary>
public class Session
{
    public int Id { get; set; }
    public string Token { get; set; } = "";
}

/// <summary>The sample's model.</summary>
public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(b =>
        {
            b.Property(p => p.Status).HasConversion<string>();
            b.Property(p => p.Price).HasPrecision(18, 2);
            b.HasIndex(p => p.Sku).IsUnique();

            // Every property is captured; these two narrow that, deliberately and per property.
            b.IsAudited(a => a
                .Mask(p => p.SupplierAccount)
                .Exclude(p => p.ScratchNotes));
        });

        modelBuilder.Entity<Session>();
    }
}
