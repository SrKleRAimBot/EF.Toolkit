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

public class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<Customer>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320).IsRequired();
            b.Property(x => x.Balance).HasPrecision(18, 2);

            // MergeBenchmarks matches on Email, and ON CONFLICT needs a unique index to define
            // what a conflict is.
            b.HasIndex(x => x.Email).IsUnique();
        });
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
}
