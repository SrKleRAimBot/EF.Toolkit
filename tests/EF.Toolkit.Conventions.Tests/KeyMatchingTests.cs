using EFToolkit.Conventions.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EFToolkit.Conventions.Tests;

/// <summary>Keys the toolkit has to match the way EF matches them: composite, and with a comparer of their own.</summary>
public sealed class KeyMatchingTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public sealed class Customer
    {
        public int TenantId { get; set; }
        public int Id { get; set; }
        public bool IsDeleted { get; set; }
        public List<Invoice> Invoices { get; } = [];
    }

    public sealed class Invoice
    {
        public int Id { get; set; }

        /// <summary>Required: EF's fixup leaves it, and nulls only CustomerId.</summary>
        public int TenantId { get; set; }

        public int? CustomerId { get; set; }
        public Customer? Customer { get; set; }
    }

    public sealed class Account
    {
        public string Code { get; set; } = "";
        public bool IsDeleted { get; set; }
        public List<Order> Orders { get; } = [];
    }

    public sealed class Order
    {
        public int Id { get; set; }
        public string AccountCode { get; set; } = "";
        public bool IsDeleted { get; set; }
    }

    public sealed class Shop(DbContextOptions<Shop> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Customer>(b =>
            {
                b.HasKey(c => new { c.TenantId, c.Id });
                b.Property(c => c.Id).ValueGeneratedNever();
                b.HasSoftDelete(c => c.IsDeleted);
                b.HasMany(c => c.Invoices).WithOne(i => i.Customer)
                    .HasForeignKey(i => new { i.TenantId, i.CustomerId })
                    .OnDelete(DeleteBehavior.ClientSetNull);
            });
            modelBuilder.Entity<Invoice>().Property(i => i.Id).ValueGeneratedNever();

            var ignoreCase = new ValueComparer<string>(
                (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase),
                s => StringComparer.OrdinalIgnoreCase.GetHashCode(s),
                s => s);
            modelBuilder.Entity<Account>(b =>
            {
                b.HasKey(a => a.Code);
                b.Property(a => a.Code).UseCollation("NOCASE").Metadata.SetValueComparer(ignoreCase);
                b.HasSoftDelete(a => a.IsDeleted);
                b.HasMany(a => a.Orders).WithOne().HasForeignKey(o => o.AccountCode).OnDelete(DeleteBehavior.ClientCascade);
            });
            modelBuilder.Entity<Order>(b =>
            {
                b.Property(o => o.Id).ValueGeneratedNever();
                b.Property(o => o.AccountCode).UseCollation("NOCASE").Metadata.SetValueComparer(ignoreCase);
                b.HasSoftDelete(o => o.IsDeleted);
            });
        }
    }

    private Shop Context()
        => new(new DbContextOptionsBuilder<Shop>()
            .UseSqlite(_connection)
            .UseEntityConventions()
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync();
        }

        await using var context = Context();
        await context.Database.EnsureCreatedAsync();
        context.Add(new Customer { TenantId = 7, Id = 1 });
        context.Add(new Invoice { Id = 1, TenantId = 7, CustomerId = 1 });
        context.Add(new Account { Code = "ABC" });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("INSERT INTO \"Order\" (\"Id\", \"AccountCode\", \"IsDeleted\") VALUES (1, 'abc', 0)");
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Soft_deleting_a_principal_keeps_a_composite_key_whose_required_part_EF_left_alone()
    {
        // Remove() nulled CustomerId and left TenantId; the repair wanted every component null, so the
        // invoice was saved cut off from a customer whose row stays.
        await using (var context = Context())
        {
            var customer = await context.Set<Customer>().Include(c => c.Invoices).SingleAsync(Ct);
            context.Remove(customer);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = Context();
        (await fresh.Set<Invoice>().SingleAsync(Ct)).CustomerId.ShouldBe(1);
    }

    [Fact]
    public async Task A_dependent_matched_by_the_key_comparer_is_hard_deleted_with_its_principal()
    {
        // EF linked the order (abc) to the account (ABC) through the key's case-insensitive comparer and
        // cascaded the delete to it. Matching that dependent to its hard-deleted principal has to use the
        // same comparer; Equals found no match. (On SQLite the row is gone either way, so this guards the
        // outcome rather than reproducing the SQL Server foreign-key failure.)
        await using (var context = Context())
        {
            var account = await context.Set<Account>().Include(a => a.Orders).SingleAsync(Ct);
            account.Orders.ShouldHaveSingleItem();
            context.Entry(account.Orders[0]).Property(o => o.AccountCode).CurrentValue.ShouldBe("abc");
            context.HardDelete(account);
            await context.SaveChangesAsync(Ct);
        }

        await using var fresh = Context();
        (await fresh.Set<Account>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);
        (await fresh.Set<Order>().IgnoreQueryFilters().CountAsync(Ct)).ShouldBe(0);
    }
}
