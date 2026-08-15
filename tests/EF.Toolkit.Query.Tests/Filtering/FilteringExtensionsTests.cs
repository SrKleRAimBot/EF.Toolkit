using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Filtering;

/// <summary>
///     Covers the conditional filters. Asserted over an in-memory queryable, so the semantics are
///     checked here and the translation is checked by the equivalence suite.
/// </summary>
public class FilteringExtensionsTests
{
    private static readonly Order[] Rows =
    [
        new() { Id = 1, Total = 10, Status = OrderStatus.Placed, PlacedAt = new DateTime(2026, 1, 1) },
        new() { Id = 2, Total = 50, Status = OrderStatus.Shipped, PlacedAt = new DateTime(2026, 2, 1) },
        new() { Id = 3, Total = 90, Status = OrderStatus.Cancelled, PlacedAt = new DateTime(2026, 3, 1) },
    ];

    private static IQueryable<Order> Query => Rows.AsQueryable();

    [Fact]
    public void WhereIf_applies_the_filter_when_the_condition_holds()
        => Ids(Query.WhereIf(true, o => o.Total > 20)).ShouldBe([2, 3]);

    [Fact]
    public void WhereIf_leaves_the_expression_tree_untouched_when_it_does_not()
    {
        // Returning the source itself rather than a no-op Where matters beyond tidiness: an extra node
        // would change the compiled-query cache key, so the same logical query would compile twice.
        var source = Query;

        source.WhereIf(false, o => o.Total > 20).ShouldBeSameAs(source);
    }

    [Fact]
    public void WhereIfNotNull_applies_the_filter_for_a_present_value_type()
        => Ids(Query.WhereIfNotNull((decimal?)50, v => o => o.Total >= v)).ShouldBe([2, 3]);

    [Fact]
    public void WhereIfNotNull_skips_the_filter_for_an_absent_value_type()
    {
        var source = Query;

        source.WhereIfNotNull((decimal?)null, v => o => o.Total >= v).ShouldBeSameAs(source);
    }

    [Fact]
    public void WhereIfNotNull_applies_the_filter_for_a_present_reference_type()
        => Ids(Query.WhereIfNotNull("x", _ => o => o.Total > 20)).ShouldBe([2, 3]);

    [Fact]
    public void WhereIfNotNull_skips_the_filter_for_an_absent_reference_type()
    {
        var source = Query;

        source.WhereIfNotNull((string?)null, _ => o => o.Total > 20).ShouldBeSameAs(source);
    }

    [Fact]
    public void WhereIn_keeps_rows_whose_column_is_listed()
        => Ids(Query.WhereIn(o => o.Status, [OrderStatus.Placed, OrderStatus.Cancelled]))
            .ShouldBe([1, 3]);

    [Fact]
    public void WhereIn_with_one_value_behaves_like_equality()
        => Ids(Query.WhereIn(o => o.Id, [2])).ShouldBe([2]);

    [Fact]
    public void WhereIn_ignores_duplicates()
        => Ids(Query.WhereIn(o => o.Id, [2, 2, 2])).ShouldBe([2]);

    [Fact]
    public void WhereIn_with_no_values_matches_nothing()
    {
        // The same answer SQL's IN () gives, and the opposite of the "no filter" an empty list is
        // sometimes taken to mean — so it is worth pinning rather than leaving to the provider.
        Ids(Query.WhereIn(o => o.Id, [])).ShouldBeEmpty();
    }

    [Fact]
    public void WhereBetween_is_inclusive_below_and_exclusive_above()
    {
        // Half-open so consecutive ranges tile exactly: [Jan, Feb) and [Feb, Mar) between them cover
        // every row once, which >= && <= does not.
        Ids(Query.WhereBetween(o => o.PlacedAt, new DateTime(2026, 1, 1), new DateTime(2026, 3, 1)))
            .ShouldBe([1, 2]);
    }

    [Fact]
    public void WhereBetween_accepts_an_open_lower_bound()
        => Ids(Query.WhereBetween(o => o.PlacedAt, null, new DateTime(2026, 2, 1))).ShouldBe([1]);

    [Fact]
    public void WhereBetween_accepts_an_open_upper_bound()
        => Ids(Query.WhereBetween(o => o.PlacedAt, new DateTime(2026, 2, 1), null)).ShouldBe([2, 3]);

    [Fact]
    public void WhereBetween_with_both_bounds_open_is_a_no_op()
    {
        var source = Query;

        source.WhereBetween(o => o.PlacedAt, null, null).ShouldBeSameAs(source);
    }

    [Fact]
    public void WhereBetween_with_inverted_bounds_matches_nothing()
    {
        // Not refused: an inverted range is a legitimate way for a caller to end up with no rows, and
        // it is the caller's own two values that produced it.
        Ids(Query.WhereBetween(o => o.PlacedAt, new DateTime(2026, 3, 1), new DateTime(2026, 1, 1)))
            .ShouldBeEmpty();
    }

    [Fact]
    public void The_filters_reject_null_arguments()
    {
        Should.Throw<ArgumentNullException>(() => Query.WhereIf(true, null!));
        Should.Throw<ArgumentNullException>(() => Query.WhereIn(o => o.Id, null!));
        Should.Throw<ArgumentNullException>(() => Query.WhereIn<Order, int>(null!, [1]));
        Should.Throw<ArgumentNullException>(
            () => Query.WhereBetween<Order, DateTime>(null!, null, null));
    }

    private static int[] Ids(IQueryable<Order> query)
        => query.Select(static o => o.Id).ToArray();
}
