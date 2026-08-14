using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Benchmarks;

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public decimal Balance { get; set; }
}

/// <summary>
///     A wide row, which is where per-column costs actually show up.
/// </summary>
/// <remarks>
///     A five-column table of primitives is the best case for every technique in this library, so
///     measuring only that hides the things worth measuring: boxing per cell, per-column type
///     resolution, converter dispatch, and the width of the copy stream itself. Thirty columns
///     spanning the type families a real schema uses — decimals with scale, GUIDs, dates and times
///     of every shape, long text, an enum through a value converter — put those costs on the chart.
/// </remarks>
public class WideCustomer
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Notes { get; set; }
    public string Region { get; set; } = "";
    public string Segment { get; set; } = "";

    public decimal Balance { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Discount { get; set; }
    public decimal LifetimeValue { get; set; }

    public int OrderCount { get; set; }
    public int ReturnCount { get; set; }
    public long ExternalId { get; set; }
    public short Priority { get; set; }

    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public float Rating { get; set; }

    public bool IsActive { get; set; }
    public bool IsVerified { get; set; }

    public Guid PublicId { get; set; }
    public Guid TenantId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateOnly BirthDate { get; set; }
    public TimeOnly PreferredContactTime { get; set; }

    /// <summary>Stored as a string, so every row pays a value conversion.</summary>
    public CustomerTier Tier { get; set; }

    public byte[] Signature { get; set; } = [];
}

public enum CustomerTier
{
    Bronze,
    Silver,
    Gold,
    Platinum
}

public class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<WideCustomer> WideCustomers => Set<WideCustomer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.Balance).HasPrecision(18, 2);

            // MergeBenchmarks matches on Email, and ON CONFLICT needs a unique index to define
            // what a conflict is.
            b.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<WideCustomer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.Notes).HasMaxLength(2000);
            b.Property(x => x.Region).HasMaxLength(64).IsRequired();
            b.Property(x => x.Segment).HasMaxLength(64).IsRequired();

            b.Property(x => x.Balance).HasPrecision(18, 2);
            b.Property(x => x.CreditLimit).HasPrecision(18, 2);
            b.Property(x => x.Discount).HasPrecision(9, 4);
            b.Property(x => x.LifetimeValue).HasPrecision(18, 2);

            b.Property(x => x.Tier).HasConversion<string>().HasMaxLength(16);

            b.HasIndex(x => x.Email).IsUnique();
        });
    }
}

internal static class Data
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Builds <paramref name="count" /> customers with deterministic values.</summary>
    public static List<Customer> Customers(int count, int startAt = 0)
        =>
        [
            .. Enumerable.Range(startAt, count).Select(i => new Customer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                CreatedAt = Epoch.AddSeconds(i),
                Balance = i * 1.25m
            })
        ];

    /// <summary>Builds <paramref name="count" /> wide customers with deterministic values.</summary>
    public static List<WideCustomer> WideCustomers(int count, int startAt = 0)
        =>
        [
            .. Enumerable.Range(startAt, count).Select(i => new WideCustomer
            {
                Name = $"Customer {i}",
                Email = $"customer{i}@example.com",
                Notes = i % 3 == 0 ? null : $"Note for customer {i}, recorded during import.",
                Region = $"region-{i % 20}",
                Segment = $"segment-{i % 7}",

                Balance = i * 1.25m,
                CreditLimit = 1000m + (i % 500),
                Discount = (i % 100) / 1000m,
                LifetimeValue = i * 13.75m,

                OrderCount = i % 97,
                ReturnCount = i % 13,
                ExternalId = 4_000_000_000L + i,
                Priority = (short)(i % 5),

                Latitude = -90 + (i % 180),
                Longitude = -180 + (i % 360),
                Rating = i % 5 + 0.5f,

                IsActive = i % 4 != 0,
                IsVerified = i % 3 == 0,

                PublicId = Deterministic(i),
                TenantId = Deterministic(i % 16),

                CreatedAt = Epoch.AddSeconds(i),
                UpdatedAt = Epoch.AddSeconds(i * 2),
                DeletedAt = i % 11 == 0 ? Epoch.AddSeconds(i * 3) : null,
                BirthDate = new DateOnly(1970, 1, 1).AddDays(i % 18_000),
                PreferredContactTime = new TimeOnly(i % 24, i % 60),

                Tier = (CustomerTier)(i % 4),
                Signature = BitConverter.GetBytes((long)i)
            })
        ];

    /// <summary>A stable GUID per index, so runs are comparable to one another.</summary>
    private static Guid Deterministic(int i)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, i);
        BitConverter.TryWriteBytes(bytes[8..], (long)i);
        return new Guid(bytes);
    }
}
