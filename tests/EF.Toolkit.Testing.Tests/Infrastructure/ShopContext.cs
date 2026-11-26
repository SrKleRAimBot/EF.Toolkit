using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests.Infrastructure;

public enum Tier
{
    Bronze = 1,
    Silver = 5,
    Gold = 9,
}

public sealed class Address
{
    public string Street { get; set; } = null!;

    public string City { get; set; } = null!;
}

public sealed class Customer
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string? Nickname { get; set; }

    public Tier Tier { get; set; }

    public DateTime CreatedAt { get; set; }

    public decimal Balance { get; set; }

    public Guid ExternalId { get; set; }

    public bool IsActive { get; set; }

    public int? LoyaltyPoints { get; set; }

    public string RowStamp { get; set; } = null!;

    public Address Address { get; set; } = null!;

    public List<Order> Orders { get; } = [];
}

public sealed class Shipping
{
    public string Carrier { get; set; } = null!;

    public string? Tracking { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public Customer Customer { get; set; } = null!;

    public decimal Total { get; set; }

    public DateOnly PlacedOn { get; set; }

    public Shipping Shipping { get; set; } = null!;

    public List<OrderLine> Lines { get; } = [];
}

public sealed class Product
{
    public int Id { get; set; }

    public string Sku { get; set; } = null!;

    public string Name { get; set; } = null!;

    public byte[] Thumbprint { get; set; } = null!;

    public List<string> Tags { get; set; } = null!;
}

public sealed class OrderLine
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order Order { get; set; } = null!;

    public int ProductId { get; set; }

    public Product Product { get; set; } = null!;

    public int Quantity { get; set; }
}

/// <summary>A self-referencing table.</summary>
public sealed class Category
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public int? ParentId { get; set; }

    public Category? Parent { get; set; }
}

/// <summary>One half of a two-table foreign-key cycle.</summary>
public sealed class Team
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public int? CaptainId { get; set; }

    public Player? Captain { get; set; }
}

/// <summary>The other half of the cycle.</summary>
public sealed class Player
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public int TeamId { get; set; }

    public Team Team { get; set; } = null!;
}

/// <summary>A natural, client-supplied string key.</summary>
public sealed class Tag
{
    [System.ComponentModel.DataAnnotations.Key]
    public string Code { get; set; } = null!;

    public string Label { get; set; } = null!;
}

/// <summary>Keyless and mapped to a view: never a table to empty.</summary>
public sealed class CustomerSummary
{
    public string Name { get; set; } = null!;

    public int OrderCount { get; set; }
}

/// <summary>Keyless and mapped to a SQL query: never a table to empty.</summary>
public sealed class RecentOrder
{
    public int Id { get; set; }
}

public class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Team> Teams => Set<Team>();

    public DbSet<Player> Players => Set<Player>();

    public DbSet<Tag> Tags => Set<Tag>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(c => c.Name).HasMaxLength(12);
            b.Property(c => c.Email).HasMaxLength(64);
            b.HasIndex(c => c.Email).IsUnique();
            b.HasIndex(c => new { c.Name, c.Tier });
            b.Property(c => c.Tier).HasConversion<string>();
            b.Property(c => c.Balance).HasPrecision(5, 2);
            b.Property(c => c.RowStamp).HasDefaultValueSql("'generated'");
            b.ComplexProperty(c => c.Address);
        });

        modelBuilder.Entity<Order>(b =>
        {
            b.OwnsOne(o => o.Shipping);
            b.Navigation(o => o.Shipping).IsRequired();
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.Property(p => p.Sku).HasMaxLength(4);
            b.HasIndex(p => p.Sku).IsUnique();
            b.Property(p => p.Thumbprint).HasMaxLength(3);

            // Oracle's MySQL provider has no primitive collections; that model is only ever planned.
            if (Database.ProviderName == "MySql.EntityFrameworkCore")
            {
                b.Ignore(p => p.Tags);
            }
        });

        modelBuilder.Entity<Team>()
            .HasOne(t => t.Captain)
            .WithMany()
            .HasForeignKey(t => t.CaptainId);

        modelBuilder.Entity<Player>()
            .HasOne(p => p.Team)
            .WithMany()
            .HasForeignKey(p => p.TeamId);

        modelBuilder.Entity<Tag>().HasKey(t => t.Code);

        modelBuilder.Entity<CustomerSummary>().HasNoKey().ToView("CustomerSummaries");
        modelBuilder.Entity<RecentOrder>().HasNoKey().ToSqlQuery("SELECT \"Id\" FROM \"Orders\"");
    }
}

/// <summary>A context whose only constructor takes something besides the options.</summary>
public sealed class FactoryOnlyContext(DbContextOptions<FactoryOnlyContext> options, string marker) : DbContext(options)
{
    public string Marker { get; } = marker;

    public DbSet<Tag> Tags => Set<Tag>();
}

/// <summary>A context taking the untyped options, the form a context meant to be subclassed takes.</summary>
public sealed class UntypedOptionsContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Tag> Tags => Set<Tag>();
}
