using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Diagnostics;

/// <summary>Covers what the advisor can read off a query's expression tree.</summary>
/// <remarks>
///     Deliberately best-effort: anything the probe cannot recognise it leaves out, so the advisor
///     under-reports rather than inventing findings. A false "missing index" against a query the probe
///     misread would teach people to ignore the whole feature.
/// </remarks>
public class QueryShapeProbeTests
{
    [Fact]
    public void An_ordering_is_read_back_in_priority_order()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders.OrderBy(o => o.PlacedAt).ThenByDescending(o => o.Id));

        shape.OrderingPaths.ShouldBe([nameof(Order.PlacedAt), nameof(Order.Id)]);
    }

    [Fact]
    public void A_second_OrderBy_supersedes_the_first()
    {
        // LINQ makes the later OrderBy primary and demotes the earlier one to a tiebreaker, so only
        // the later one describes what the server actually sorts by first.
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders.OrderBy(o => o.PlacedAt).OrderBy(o => o.Total));

        shape.OrderingPaths.ShouldBe([nameof(Order.Total)]);
    }

    [Fact]
    public void An_unordered_query_reports_no_ordering()
    {
        using var context = TestModel.Context();

        Inspect(context.Orders.Where(o => o.Total > 1)).OrderingPaths.ShouldBeEmpty();
    }

    [Fact]
    public void Equality_filters_are_collected_from_conjunctions()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders
            .Where(o => o.CustomerId == 5 && o.Status == OrderStatus.Placed)
            .OrderBy(o => o.PlacedAt));

        shape.EqualityPaths.Order(StringComparer.Ordinal)
            .ShouldBe([nameof(Order.CustomerId), nameof(Order.Status)]);
    }

    [Fact]
    public void Filters_across_separate_Where_calls_are_all_collected()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders
            .Where(o => o.CustomerId == 5)
            .Where(o => o.Status == OrderStatus.Placed));

        shape.EqualityPaths.Count.ShouldBe(2);
    }

    [Fact]
    public void A_range_filter_is_not_mistaken_for_an_equality_one()
    {
        // Only equality pins a column to one value, and only a pinned column can sit ahead of the
        // ordering in an index prefix. Counting a range filter there would report indexes as covering
        // queries they cannot serve.
        using var context = TestModel.Context();

        Inspect(context.Orders.Where(o => o.Total > 100)).EqualityPaths.ShouldBeEmpty();
    }

    [Fact]
    public void A_disjunction_is_not_collected()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders.Where(o => o.CustomerId == 5 || o.CustomerId == 6));

        shape.EqualityPaths.ShouldBeEmpty();
    }

    [Fact]
    public void An_Include_is_noticed()
    {
        using var context = TestModel.Context();

        Inspect(context.Customers.Include(c => c.Orders)).HasInclude.ShouldBeTrue();
    }

    [Fact]
    public void AsSplitQuery_is_noticed()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Customers.Include(c => c.Orders).AsSplitQuery());

        shape.HasInclude.ShouldBeTrue();
        shape.IsSplitQuery.ShouldBeTrue();
    }

    [Fact]
    public void A_plain_query_reports_neither()
    {
        using var context = TestModel.Context();

        var shape = Inspect(context.Orders);

        shape.HasInclude.ShouldBeFalse();
        shape.IsSplitQuery.ShouldBeFalse();
        shape.OrderingPaths.ShouldBeEmpty();
        shape.EqualityPaths.ShouldBeEmpty();
    }

    private static QueryShape Inspect<T>(IQueryable<T> query)
        => QueryShapeProbe.Inspect(query.Expression);
}
