using EFToolkit.Query.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Query.Tests.Infrastructure;

/// <summary>Builds contexts for the unit tests.</summary>
/// <remarks>
///     The connection string never connects. These tests read <c>context.Model</c> and build
///     expression trees; none of them execute a query, so a real provider against an unreachable
///     server gives the genuine metadata a stub provider would not.
/// </remarks>
public static class TestModel
{
    public static QueryTestContext Context(
        Action<QueryOptionsBuilder>? configure = null,
        Action<ModelBuilder>? onModelCreating = null,
        bool useQueryHelpers = true)
    {
        var builder = new DbContextOptionsBuilder<QueryTestContext>()
            .UseSqlServer("Server=none;Database=none")
            // Every context here shares one CLR type and supplies its model through a delegate, so
            // EF's default cache key — the context type — would hand the second test the first
            // test's model.
            .ReplaceService<IModelCacheKeyFactory, PerInstanceModelCacheKeyFactory>();

        if (useQueryHelpers)
        {
            builder.UseQueryHelpers(configure);
        }

        return new QueryTestContext(builder.Options, onModelCreating);
    }

    /// <summary>The settings the context resolved, for asserting on what configuration produced.</summary>
    public static QueryOptions Options(this QueryTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetService<QueryOptions>();
    }
}

public class QueryTestContext(
    DbContextOptions<QueryTestContext> options,
    Action<ModelBuilder>? onModelCreating = null) : DbContext(options)
{
    internal object ModelCacheKey { get; } = new();

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Shipment> Shipments => Set<Shipment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Reference).HasMaxLength(64).IsRequired();
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.HasOne(x => x.Customer).WithMany(x => x.Orders).HasForeignKey(x => x.CustomerId);
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        });

        modelBuilder.Entity<Shipment>(b =>
        {
            b.HasKey(x => x.Id);
        });

        onModelCreating?.Invoke(modelBuilder);
    }
}

public sealed class PerInstanceModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (((QueryTestContext)context).ModelCacheKey, designTime);
}
