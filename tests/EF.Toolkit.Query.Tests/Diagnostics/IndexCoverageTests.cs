using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Query.Tests.Diagnostics;

/// <summary>Covers the prefix matching that decides whether a declared index could serve a query.</summary>
public class IndexCoverageTests
{
    [Fact]
    public void The_primary_key_covers_an_ordering_by_the_key()
        => IndexCoverage.IsCovered(OrderType(), [], [nameof(Order.Id)]).ShouldBeTrue();

    [Fact]
    public void An_index_leading_with_the_ordering_covers_it()
    {
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => new { x.PlacedAt, x.Id }));

        IndexCoverage.IsCovered(entityType, [], [nameof(Order.PlacedAt), nameof(Order.Id)])
            .ShouldBeTrue();
    }

    [Fact]
    public void An_index_in_the_wrong_column_order_does_not_cover_it()
    {
        // A composite index is only useful as a prefix. Ordering by (PlacedAt, Id) against an index on
        // (Id, PlacedAt) still sorts the whole matching set.
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => new { x.Id, x.PlacedAt }));

        IndexCoverage.IsCovered(entityType, [], [nameof(Order.PlacedAt), nameof(Order.Id)])
            .ShouldBeFalse();
    }

    [Fact]
    public void Equality_columns_come_first_and_in_any_order()
    {
        // Each equality column is pinned to a single value, so the server can walk the remaining
        // columns in index order whichever way round the filter named them.
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => new { x.CustomerId, x.Status, x.PlacedAt }));

        IndexCoverage.IsCovered(
                entityType,
                [nameof(Order.Status), nameof(Order.CustomerId)],
                [nameof(Order.PlacedAt)])
            .ShouldBeTrue();
    }

    [Fact]
    public void An_index_missing_one_equality_column_does_not_cover_the_query()
    {
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => new { x.CustomerId, x.PlacedAt }));

        IndexCoverage.IsCovered(
                entityType,
                [nameof(Order.CustomerId), nameof(Order.Status)],
                [nameof(Order.PlacedAt)])
            .ShouldBeFalse();
    }

    [Fact]
    public void An_ordering_column_already_pinned_by_a_filter_need_not_appear_again()
    {
        // Ordering by a column every row shares contributes nothing to the sort, so an index that
        // stops after the equality prefix still serves the query.
        var entityType = OrderType(static b => b.Entity<Order>().HasIndex(x => x.CustomerId));

        IndexCoverage.IsCovered(
                entityType,
                [nameof(Order.CustomerId)],
                [nameof(Order.CustomerId), nameof(Order.Id)])
            .ShouldBeFalse();

        IndexCoverage.IsCovered(entityType, [nameof(Order.CustomerId)], [nameof(Order.CustomerId)])
            .ShouldBeTrue();
    }

    [Fact]
    public void A_query_with_neither_filter_nor_ordering_is_trivially_covered()
        => IndexCoverage.IsCovered(OrderType(), [], []).ShouldBeTrue();

    [Fact]
    public void An_uncovered_ordering_is_reported_as_uncovered()
        => IndexCoverage.IsCovered(OrderType(), [], [nameof(Order.Total)]).ShouldBeFalse();

    [Fact]
    public void An_ordering_covering_the_primary_key_is_total()
        => IndexCoverage.IsTotalOrdering(OrderType(), [nameof(Order.PlacedAt), nameof(Order.Id)])
            .ShouldBeTrue();

    [Fact]
    public void An_ordering_covering_a_unique_index_is_total()
    {
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => x.Reference).IsUnique());

        IndexCoverage.IsTotalOrdering(entityType, [nameof(Order.Reference)]).ShouldBeTrue();
    }

    [Fact]
    public void An_ordering_covering_only_a_non_unique_index_is_not_total()
    {
        var entityType = OrderType(static b => b.Entity<Order>().HasIndex(x => x.Reference));

        IndexCoverage.IsTotalOrdering(entityType, [nameof(Order.Reference)]).ShouldBeFalse();
    }

    [Fact]
    public void An_empty_ordering_is_not_total()
        => IndexCoverage.IsTotalOrdering(OrderType(), []).ShouldBeFalse();

    [Fact]
    public void Describe_lists_the_declared_indexes_for_the_advisory_message()
    {
        var entityType = OrderType(static b =>
            b.Entity<Order>().HasIndex(x => new { x.PlacedAt, x.Id }));

        var described = IndexCoverage.Describe(entityType);

        described.ShouldContain(nameof(Order.Id));
        described.ShouldContain(nameof(Order.PlacedAt));
    }

    private static IEntityType OrderType(Action<ModelBuilder>? onModelCreating = null)
    {
        using var context = TestModel.Context(onModelCreating: onModelCreating);
        return context.Model.FindEntityType(typeof(Order))!;
    }
}
