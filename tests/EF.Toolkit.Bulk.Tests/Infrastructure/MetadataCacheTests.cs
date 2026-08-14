using EFToolkit.Bulk.Api;
using EFToolkit.Bulk.Infrastructure;
using EFToolkit.Bulk.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Infrastructure;

/// <summary>
///     Where everything derived from the model is cached.
/// </summary>
/// <remarks>
///     These were static dictionaries keyed by the metadata object, which made each one a GC root
///     for every model the process had ever built. They are runtime annotations now, so the derived
///     value is reachable only from the metadata it was derived from and is collected with it. That
///     is a property of the object graph rather than of any observable behaviour, so it is asserted
///     directly: the value is still built once, and it is on the metadata object.
/// </remarks>
public class MetadataCacheTests
{
    [Fact]
    public void A_column_plan_is_built_once_and_kept_on_its_entity_type()
    {
        using var context = new CatalogContext();
        var entityType = context.Model.FindEntityType(typeof(Product))!;

        var first = BulkEntityPlan.For(entityType, EntityState.Added);
        var second = BulkEntityPlan.For(entityType, EntityState.Added);

        second.ShouldBeSameAs(first);
        Annotation(entityType, BulkAnnotations.Plan(EntityState.Added)).ShouldBeSameAs(first);
    }

    // The plan is state-specific, so each state has to be a cache entry of its own rather than the
    // states sharing one and overwriting each other.
    [Fact]
    public void Each_entity_state_gets_its_own_entry()
    {
        using var context = new CatalogContext();
        var entityType = context.Model.FindEntityType(typeof(Product))!;

        var added = BulkEntityPlan.For(entityType, EntityState.Added);
        var modified = BulkEntityPlan.For(entityType, EntityState.Modified);

        modified.ShouldNotBeSameAs(added);
        Annotation(entityType, BulkAnnotations.Plan(EntityState.Added)).ShouldBeSameAs(added);
        Annotation(entityType, BulkAnnotations.Plan(EntityState.Modified)).ShouldBeSameAs(modified);
    }

    [Fact]
    public void A_compiled_accessor_is_kept_on_its_property()
    {
        using var context = new CatalogContext();
        var entityType = context.Model.FindEntityType(typeof(Product))!;

        var plan = BulkEntityPlan.For(entityType, EntityState.Added);
        var name = entityType.FindProperty(nameof(Product.Name))!;
        var key = entityType.FindProperty(nameof(Product.Id))!;

        Annotation(name, BulkAnnotations.Getter).ShouldNotBeNull();

        // The key is store-generated, so it is read back and needs a setter as well.
        Annotation(key, BulkAnnotations.Setter).ShouldNotBeNull();

        // And the plan hands out exactly what is cached, rather than a fresh compilation.
        Annotation(name, BulkAnnotations.Getter)
            .ShouldBeSameAs(plan.Getters[Ordinal(plan, nameof(Product.Name))]);
    }

    // A plan built for explicit match columns deliberately is not cached -- they vary per call --
    // which is why the accessors are cached one level down, on the properties.
    [Fact]
    public void An_uncached_merge_plan_still_reuses_the_cached_accessors()
    {
        using var context = new CatalogContext();
        var entityType = context.Model.FindEntityType(typeof(Product))!;
        var match = new[] { entityType.FindProperty(nameof(Product.Sku))! };

        var first = BulkEntityPlan.For(entityType, EntityState.Added, match, null);
        var second = BulkEntityPlan.For(entityType, EntityState.Added, match, null);

        second.ShouldNotBeSameAs(first);
        second.Getters[Ordinal(second, nameof(Product.Name))]
            .ShouldBeSameAs(first.Getters[Ordinal(first, nameof(Product.Name))]);
    }

    [Fact]
    public void A_graph_plan_is_built_once_and_kept_on_its_entity_type()
    {
        using var context = new CatalogContext();
        var entityType = context.Model.FindEntityType(typeof(Product))!;

        var first = EntityGraphPlan.For(entityType);

        EntityGraphPlan.For(entityType).ShouldBeSameAs(first);
        Annotation(entityType, BulkAnnotations.GraphPlan).ShouldBeSameAs(first);
    }

    [Fact]
    public void The_topological_order_is_sorted_once_and_kept_on_the_model()
    {
        using var context = new CatalogContext();

        var first = EntityTypeGraph.TopologicalOrder(context.Model);

        EntityTypeGraph.TopologicalOrder(context.Model).ShouldBeSameAs(first);
        Annotation(context.Model, BulkAnnotations.TopologicalOrder).ShouldBeSameAs(first);
    }

    private static object? Annotation(IAnnotatable annotatable, string name)
        => annotatable.FindRuntimeAnnotation(name)?.Value;

    private static int Ordinal(BulkEntityPlan plan, string property)
    {
        for (var i = 0; i < plan.Columns.Count; i++)
        {
            if (plan.Columns[i].Property?.Name == property)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"'{property}' is not in the plan.");
    }

    private sealed class Product
    {
        public int Id { get; set; }
        public string Sku { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Category? Category { get; set; }
        public int CategoryId { get; set; }
    }

    private sealed class Category
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class CatalogContext : DbContext
    {
        // Never opened: everything under test is derived from the model alone.
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlServer("Server=none;Database=none");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Category>();
            modelBuilder.Entity<Product>();
        }
    }
}
