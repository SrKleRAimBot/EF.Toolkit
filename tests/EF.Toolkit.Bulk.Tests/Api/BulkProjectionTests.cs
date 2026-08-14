using System.Linq.Expressions;
using EFToolkit.Bulk.Api;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Api;

/// <summary>
///     What <c>Include</c>, <c>Exclude</c>, <c>InsertOnly</c> and a non-key <c>MatchOn</c> do to the
///     plan, which is where every one of them takes effect.
/// </summary>
/// <remarks>
///     Asserted on the plan rather than against a database because the plan is what decides the
///     statement: a column's roles here are exactly the write list, the join predicate and the
///     update arm the executors go on to build.
/// </remarks>
public class BulkProjectionTests
{
    [Fact]
    public void Include_narrows_an_update_to_the_named_columns()
    {
        var plan = Plan<Product>(EntityState.Modified, BulkOperationKind.Update, o => o.Include(p => p.Name));

        Written(plan).ShouldBe([nameof(Product.Name)]);

        // The key still locates the row; a projection narrows what is written, not what is matched.
        Condition(plan).ShouldBe([nameof(Product.Id)]);
    }

    [Fact]
    public void Exclude_drops_only_the_named_columns()
    {
        var plan = Plan<Product>(
            EntityState.Modified, BulkOperationKind.Update, o => o.Exclude(p => p.CreatedAt));

        Written(plan).ShouldBe(
            [nameof(Product.Sku), nameof(Product.Name), nameof(Product.Price)], ignoreOrder: true);
    }

    [Fact]
    public void Include_does_not_make_a_store_generated_column_writable()
    {
        // Naming it can only narrow. Writing to an identity column from the insert path would fail
        // at the server, and the caller has no value for it to begin with.
        var plan = Plan<Product>(
            EntityState.Added,
            BulkOperationKind.Insert,
            o => o.Include(p => new { p.Id, p.Name }));

        Written(plan).ShouldBe([nameof(Product.Name)]);
    }

    [Fact]
    public void Include_and_Exclude_together_are_refused()
    {
        Should.Throw<BulkNotSupportedException>(
                () => Plan<Product>(
                    EntityState.Modified,
                    BulkOperationKind.Update,
                    o => o.Include(p => p.Name).Exclude(p => p.Price)))
            .Message.ShouldContain("cannot both be used");
    }

    [Fact]
    public void Excluding_a_key_from_an_insert_is_refused()
    {
        // Silently inserting rows with no key is not an option, and neither is quietly ignoring
        // what the caller asked for.
        Should.Throw<BulkNotSupportedException>(
                () => Plan<Coded>(
                    EntityState.Added,
                    BulkOperationKind.Insert,
                    o => o.Exclude(p => p.Code)))
            .Message.ShouldContain("part of the key");
    }

    [Fact]
    public void Excluding_a_match_column_from_a_merge_is_refused()
    {
        Should.Throw<BulkNotSupportedException>(
                () => Plan<Product>(
                    EntityState.Added,
                    BulkOperationKind.Merge,
                    o => o.MatchOn(p => p.Sku).Exclude(p => p.Sku)))
            .Message.ShouldContain("match columns");
    }

    [Fact]
    public void InsertOnly_is_refused_on_anything_but_a_merge()
    {
        Should.Throw<BulkNotSupportedException>(
                () => Plan<Product>(
                    EntityState.Modified,
                    BulkOperationKind.Update,
                    o => o.InsertOnly(p => p.CreatedAt)))
            .Message.ShouldContain("InsertOnly has no meaning");
    }

    [Fact]
    public void InsertOnly_marks_the_column_without_removing_it_from_the_write_set()
    {
        var plan = Plan<Product>(
            EntityState.Added,
            BulkOperationKind.Merge,
            o => o.MatchOn(p => p.Sku).InsertOnly(p => p.CreatedAt));

        // Still written -- the insert arm needs a value for it -- and flagged so the update arm
        // leaves the row's original value alone.
        Written(plan).ShouldContain(nameof(Product.CreatedAt));
        plan.Columns.Single(c => c.Property?.Name == nameof(Product.CreatedAt))
            .IsInsertOnly.ShouldBeTrue();
        plan.Columns.Single(c => c.Property?.Name == nameof(Product.Name))
            .IsInsertOnly.ShouldBeFalse();
    }

    [Fact]
    public void An_update_left_with_nothing_to_write_is_refused()
    {
        // Left alone this reaches the executor's decline and falls back to stock EF, which writes
        // every column -- the opposite of what the projection asked for.
        Should.Throw<BulkNotSupportedException>(
                () => Plan<Product>(
                    EntityState.Modified,
                    BulkOperationKind.Update,
                    o => o.Include(p => p.Id)))
            .Message.ShouldContain("no columns left to write");
    }

    [Fact]
    public void MatchOn_moves_an_update_off_the_key()
    {
        var plan = Plan<Product>(EntityState.Modified, BulkOperationKind.Update, o => o.MatchOn(p => p.Sku));

        Condition(plan).ShouldBe([nameof(Product.Sku)]);

        // The key is neither matched on nor written -- reassigning it would turn an update into a
        // silent re-key -- so it drops out of the plan altogether and is never staged.
        plan.Columns.Select(c => c.Property!.Name).ShouldNotContain(nameof(Product.Id));
        Written(plan).ShouldBe(
            [nameof(Product.Name), nameof(Product.Price), nameof(Product.CreatedAt)],
            ignoreOrder: true);
    }

    [Fact]
    public void MatchOn_moves_a_delete_off_the_key()
    {
        var plan = Plan<Product>(EntityState.Deleted, BulkOperationKind.Delete, o => o.MatchOn(p => p.Sku));

        Condition(plan).ShouldBe([nameof(Product.Sku)]);
        Written(plan).ShouldBeEmpty();
    }

    [Fact]
    public void A_composite_MatchOn_stages_every_named_column()
    {
        var plan = Plan<Product>(
            EntityState.Modified,
            BulkOperationKind.Update,
            o => o.MatchOn(p => new { p.Sku, p.Name }));

        Condition(plan).ShouldBe([nameof(Product.Sku), nameof(Product.Name)], ignoreOrder: true);
        Written(plan).ShouldBe([nameof(Product.Price), nameof(Product.CreatedAt)], ignoreOrder: true);
    }

    private static BulkEntityPlan Plan<TEntity>(
        EntityState state,
        BulkOperationKind kind,
        Action<BulkOperationOptionsBuilder<TEntity>> configure)
        where TEntity : class
    {
        using var context = new CatalogContext();

        var entityType = context.Model.FindEntityType(typeof(TEntity))!;

        var builder = new BulkOperationOptionsBuilder<TEntity>(BulkOperationOptions.Default);
        configure(builder);
        var options = builder.Options;

        var match = options.Match is Expression<Func<TEntity, object?>> selector
            ? MatchPropertySelector.Resolve(entityType, selector)
            : null;

        return BulkEntityPlan.For(
            entityType,
            state,
            match ?? (kind is BulkOperationKind.Merge or BulkOperationKind.Synchronize
                ? entityType.FindPrimaryKey()!.Properties
                : null),
            BulkProjection.Resolve<TEntity>(entityType, options, kind));
    }

    private static List<string> Written(BulkEntityPlan plan)
        => [.. plan.Columns.Where(c => c.IsWrite).Select(c => c.Property!.Name)];

    private static List<string> Condition(BulkEntityPlan plan)
        => [.. plan.Columns.Where(c => c.IsCondition).Select(c => c.Property!.Name)];

    private sealed class Product
    {
        public int Id { get; set; }
        public string Sku { get; set; } = "";
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>A client-supplied key, so excluding it is something the caller can actually try.</summary>
    private sealed class Coded
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class CatalogContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            // Never opened: a plan is built from the model alone.
            => optionsBuilder.UseSqlServer("Server=none;Database=none");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>();
            modelBuilder.Entity<Coded>().HasKey(c => c.Code);
        }
    }
}
