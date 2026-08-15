using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Filtering;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Sorting;
using Microsoft.EntityFrameworkCore;

// Shouldly declares a SortDirection of its own, and the global usings bring both into scope.
using SortDirection = EFToolkit.Query.Sorting.SortDirection;

namespace EFToolkit.Query.Equivalence;

/// <summary>
///     Asserts that the sorting and filtering helpers translate, and return what hand-written LINQ
///     returns.
/// </summary>
/// <remarks>
///     The unit tests establish the semantics against in-memory rows. What they cannot show is that
///     the expression trees reach the provider in a shape it can translate — an <c>Invoke</c> node
///     left behind by a bad splice, or a comparison EF has no SQL for, fails only here.
/// </remarks>
public abstract class SortingAndFilteringTests(QueryDatabaseFixture fixture)
{
    private static readonly SortSpecification<Order> OrderSort = SortSpecification.For<Order>(s => s
        .Allow("placed", o => o.PlacedAt)
        .Allow("total", o => o.Total)
        .Allow("reference", o => o.Reference)
        .DefaultOrder("placed", SortDirection.Descending)
        .Tiebreaker(o => o.Id));

    private static readonly SearchSpecification<Order> OrderSearch = SearchSpecification.For<Order>(s => s
        .Field(o => o.Reference)
        .Field(o => o.Note));

    [Theory]
    [InlineData("placed")]
    [InlineData("placed:desc")]
    [InlineData("total:desc,placed")]
    [InlineData("reference,total:desc")]
    [InlineData(null)]
    public async Task Every_allowed_ordering_translates_and_is_stable(string? sort)
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var first = await context.Orders.OrderBy(OrderSort, sort).Select(o => o.Id).ToListAsync(token);
        var second = await context.Orders.OrderBy(OrderSort, sort).Select(o => o.Id).ToListAsync(token);

        first.Count.ShouldBe(30);

        // The tiebreaker's whole job: PlacedAt and Total both repeat heavily, so without it the engine
        // is free to answer these two identical queries in different orders.
        second.ShouldBe(first);
    }

    [Fact]
    public async Task An_ordering_from_a_specification_pages_correctly()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var ordered = context.Orders.OrderBy(OrderSort, "total:desc");
        var seen = new List<int>();

        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await ordered.ToPagedResultAsync(context, PageRequest.Of(pageNumber, 7), token);
            seen.AddRange(page.Items.Select(static o => o.Id));

            if (page.HasNext != true)
            {
                break;
            }
        }

        seen.Distinct().Count().ShouldBe(30);
    }

    [Fact]
    public async Task WhereIn_translates_and_matches_hand_written_LINQ()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        OrderStatus[] wanted = [OrderStatus.Placed, OrderStatus.Cancelled];

        var viaHelper = await context.Orders
            .WhereIn(context, o => o.Status, wanted)
            .Select(o => o.Id)
            .OrderBy(id => id)
            .ToListAsync(token);

        var viaLinq = await context.Orders
            .Where(o => wanted.Contains(o.Status))
            .Select(o => o.Id)
            .OrderBy(id => id)
            .ToListAsync(token);

        viaHelper.ShouldBe(viaLinq);
        viaHelper.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task WhereIn_with_no_values_returns_nothing_rather_than_failing_to_translate()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        var rows = await context.Orders
            .WhereIn(o => o.Status, [])
            .ToListAsync(token);

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhereBetween_translates_and_tiles_without_overlap()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var boundary = Seed.Epoch.AddDays(3);

        var below = await context.Orders
            .WhereBetween(o => o.PlacedAt, null, boundary)
            .Select(o => o.Id)
            .ToListAsync(token);

        var atOrAbove = await context.Orders
            .WhereBetween(o => o.PlacedAt, boundary, null)
            .Select(o => o.Id)
            .ToListAsync(token);

        // Half-open, so the two halves partition the set exactly: nothing counted twice, nothing lost.
        below.Intersect(atOrAbove).ShouldBeEmpty();
        below.Count.ShouldBeGreaterThan(0);
        atOrAbove.Count.ShouldBeGreaterThan(0);
        (below.Count + atOrAbove.Count).ShouldBe(30);
    }

    [Fact]
    public async Task Search_translates_across_a_nullable_field()
    {
        // Note is null on every fourth row. The predicate's null guard has to survive translation, and
        // the rows with nulls must simply not match rather than fail the query.
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var rows = await context.Orders.Search(OrderSearch, "REF-01").ToListAsync(token);

        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(o => o.Reference == "REF-01");
    }

    [Fact]
    public async Task A_blank_search_term_returns_everything()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        (await context.Orders.Search(OrderSearch, "  ").CountAsync(token)).ShouldBe(30);
    }

    [Fact]
    public async Task Composed_predicates_translate()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var predicate = Predicates.True<Order>()
            .And(o => o.Total > 50m)
            .Or(o => o.Status == OrderStatus.Cancelled);

        var viaHelper = await context.Orders.Where(predicate).Select(o => o.Id).OrderBy(id => id)
            .ToListAsync(token);

        var viaLinq = await context.Orders
            .Where(o => o.Total > 50m || o.Status == OrderStatus.Cancelled)
            .Select(o => o.Id)
            .OrderBy(id => id)
            .ToListAsync(token);

        viaHelper.ShouldBe(viaLinq);
    }

    [Fact]
    public async Task Conditional_filters_compose_into_one_translatable_query()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        decimal? minimum = 50m;
        string? note = null;

        var rows = await context.Orders
            .WhereIf(condition: true, o => o.Status != OrderStatus.Cancelled)
            .WhereIfNotNull(minimum, v => o => o.Total >= v)
            .WhereIfNotNull(note, v => o => o.Note == v)
            .Select(o => o.Id)
            .ToListAsync(token);

        rows.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_unknown_sort_field_is_refused_before_the_query_runs()
    {
        var (context, token) = await SeededAsync(5);
        await using var _ = context;

        Should.Throw<QueryNotSupportedException>(() => context.Orders.OrderBy(OrderSort, "note"));
        await Task.CompletedTask;
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededAsync(int orderCount)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext();

        if (orderCount > 0)
        {
            await Seed.OrdersAsync(context, orderCount, token);
        }

        context.ChangeTracker.Clear();
        return (context, token);
    }
}
