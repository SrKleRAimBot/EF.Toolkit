using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence;

/// <summary>
///     Walks real result sets on a real engine and asserts every row is visited exactly once, in the
///     ordering's own order.
/// </summary>
/// <remarks>
///     Bound to each engine by a thin sealed subclass, so the scenarios are written once and run
///     against every database the package supports. That matters more here than elsewhere: the whole
///     reason the predicate is built as an OR-of-ANDs rather than as a row-value comparison is that
///     the engines do not agree about the latter.
/// </remarks>
public abstract class KeysetPagingTests(QueryDatabaseFixture fixture)
{
    private static readonly KeysetDefinition<Order> ByPlacedThenId = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Order> ByPlacedDescThenId = KeysetDefinition.For<Order>(k => k
        .Descending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Order> ByTotalThenPlacedThenId = KeysetDefinition.For<Order>(k => k
        .Descending(o => o.Total)
        .Ascending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Order> ByReferenceThenId = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.Reference)
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Order> ByStatusThenId = KeysetDefinition.For<Order>(k => k
        .Descending(o => o.Status)
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Shipment> ByShipmentId = KeysetDefinition.For<Shipment>(k => k
        .Ascending(s => s.Id));

    private static readonly KeysetDefinition<Shipment> ByDispatchedThenId = KeysetDefinition.For<Shipment>(k => k
        .Descending(s => s.DispatchedAt)
        .Ascending(s => s.Id));

    private static readonly KeysetDefinition<Employee> ByEmployeeId = KeysetDefinition.For<Employee>(k => k
        .Ascending(e => e.Id)
        .AllowConvertedKey());

    private static readonly KeysetDefinition<Employee> ByHiredThenEmployeeId = KeysetDefinition.For<Employee>(k => k
        .Descending(e => e.HiredOn)
        .Ascending(e => e.Id)
        .AllowConvertedKey());

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(20)]
    public async Task An_ascending_walk_visits_every_row_exactly_once(int pageSize)
    {
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByPlacedThenId, static o => o.Id, pageSize, token);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public async Task A_descending_walk_visits_every_row_exactly_once(int pageSize)
    {
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByPlacedDescThenId, static o => o.Id, pageSize, token);
    }

    [Fact]
    public async Task A_three_component_mixed_direction_walk_visits_every_row_exactly_once()
    {
        // Total repeats every three rows and PlacedAt every five, so both leading components tie
        // repeatedly and the ordering only resolves on the trailing key.
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByTotalThenPlacedThenId, static o => o.Id, 4, token);
    }

    [Fact]
    public async Task A_string_leading_column_walks_correctly()
    {
        // string has no > operator, so the comparison goes through CompareTo. Whether EF turns that
        // into a plain SQL comparison is an engine question, which is why it is asserted here.
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByReferenceThenId, static o => o.Id, 6, token);
    }

    [Fact]
    public async Task An_enum_leading_column_walks_correctly()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByStatusThenId, static o => o.Id, 4, token);
    }

    [Fact]
    public async Task A_Guid_key_walks_in_the_engine_s_own_order()
    {
        // The database's ordering of uniqueidentifier is not .NET's. Keyset paging only needs the
        // comparison and the ORDER BY to agree with each other, and both run on the server — so this
        // has to hold even though the sequence looks arbitrary from the client.
        var (context, token) = await SeededShipmentsAsync(25);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Shipments, ByShipmentId, static s => s.Id, 4, token);
    }

    [Fact]
    public async Task A_DateTimeOffset_column_survives_the_cursor_round_trip()
    {
        var (context, token) = await SeededShipmentsAsync(25);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Shipments, ByDispatchedThenId, static s => s.Id, 4, token);
    }

    [Fact]
    public async Task A_decimal_boundary_value_survives_the_cursor_round_trip()
    {
        // Total carries two decimal places in a column declared with four. A cursor that rendered the
        // boundary at the column's scale would still compare equal here, but one that rendered it at
        // the wrong scale — or through the current culture — would not, and the page after the
        // boundary would start in the wrong place.
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var first = await context.Orders.ToKeysetPageAsync(
            context, ByTotalThenPlacedThenId, 5, cancellationToken: token);

        var second = await context.Orders.ToKeysetPageAsync(
            context, ByTotalThenPlacedThenId, 5, first.Next, token);

        second.Items.Select(static o => o.Id)
            .Intersect(first.Items.Select(static o => o.Id))
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public async Task A_value_converted_key_walks_every_row_exactly_once(int pageSize)
    {
        // The cursor carries the stored text, and the comparison is written against the property and
        // converted by EF on the way to SQL. If those two ever disagree about what the boundary is,
        // the walk drops the row sitting on it — which is what this asserts against the real engine.
        var (context, token) = await SeededEmployeesAsync(25);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Employees, ByEmployeeId, static e => e.Id, pageSize, token);
    }

    [Fact]
    public async Task A_value_converted_key_breaks_ties_behind_another_column()
    {
        // Three employees per hire date, so every page boundary the leading column reaches is a tie
        // that only the converted key resolves.
        var (context, token) = await SeededEmployeesAsync(25);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Employees, ByHiredThenEmployeeId, static e => e.Id, 4, token);
    }

    [Fact]
    public async Task A_filtered_query_walks_only_the_matching_rows()
    {
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        var filtered = context.Orders.Where(o => o.Status == OrderStatus.Shipped);

        await PagingEquivalence.AssertAsync(
            context, filtered, ByPlacedThenId, static o => o.Id, 3, token);
    }

    [Fact]
    public async Task An_empty_set_returns_an_empty_page_with_no_cursors()
    {
        var (context, token) = await SeededAsync(0);
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 10, cancellationToken: token);

        page.Items.ShouldBeEmpty();
        page.IsEmpty.ShouldBeTrue();
        page.HasNext.ShouldBeFalse();
        page.HasPrevious.ShouldBeFalse();
        page.Next.ShouldBeNull();
        page.Previous.ShouldBeNull();
    }

    [Fact]
    public async Task A_set_that_fits_on_one_page_reports_no_neighbours()
    {
        var (context, token) = await SeededAsync(5);
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 10, cancellationToken: token);

        page.Items.Count.ShouldBe(5);
        page.HasNext.ShouldBeFalse();
        page.HasPrevious.ShouldBeFalse();
        page.Next.ShouldBeNull();
    }

    [Fact]
    public async Task A_set_exactly_filling_one_page_does_not_claim_another()
    {
        // The off-by-one worth pinning: fetching pageSize + 1 rows and finding exactly pageSize must
        // report no next page, not an empty one.
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 10, cancellationToken: token);

        page.Items.Count.ShouldBe(10);
        page.HasNext.ShouldBeFalse();
    }

    [Fact]
    public async Task The_first_page_reports_nothing_before_it()
    {
        var (context, token) = await SeededAsync(20);
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 5, cancellationToken: token);

        page.HasPrevious.ShouldBeFalse();
        page.Previous.ShouldBeNull();
        page.HasNext.ShouldBeTrue();
    }

    [Fact]
    public async Task Stepping_forward_then_back_returns_to_the_same_page()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var first = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 6, cancellationToken: token);

        var second = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 6, first.Next, token);

        var backAgain = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 6, second.Previous, token);

        backAgain.Items.Select(static o => o.Id).ShouldBe(first.Items.Select(static o => o.Id));
    }

    [Fact]
    public async Task A_row_inserted_past_the_boundary_is_visited_and_one_before_it_is_not_repeated()
    {
        // The property offset paging cannot offer. Inserting a row behind the boundary shifts nothing,
        // because the cursor names a position in the ordering rather than a count from the start.
        var (context, token) = await SeededAsync(20);
        await using var _ = context;

        var first = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 5, cancellationToken: token);

        context.Orders.Add(new Order
        {
            PlacedAt = Seed.Epoch.AddDays(-1),
            Total = 1m,
            Status = OrderStatus.Placed,
            CustomerId = first.Items[0].CustomerId,
            Reference = "REF-EARLY",
        });

        await context.SaveChangesAsync(token);

        var second = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 5, first.Next, token);

        second.Items.Select(static o => o.Id)
            .Intersect(first.Items.Select(static o => o.Id))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task A_page_size_beyond_the_configured_ceiling_is_clamped()
    {
        var (context, token) = await SeededAsync(30, q => q.DefaultPageSize(4).MaxPageSize(4));
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 1_000, cancellationToken: token);

        page.PageSize.ShouldBe(4);
        page.Items.Count.ShouldBe(4);
    }

    [Fact]
    public async Task An_unset_page_size_uses_the_configured_default()
    {
        var (context, token) = await SeededAsync(30, q => q.DefaultPageSize(6));
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, cancellationToken: token);

        page.Items.Count.ShouldBe(6);
    }

    [Fact]
    public async Task A_cursor_from_a_different_ordering_is_refused()
    {
        var (context, token) = await SeededAsync(20);
        await using var _ = context;

        var page = await context.Orders.ToKeysetPageAsync(
            context, ByPlacedThenId, 5, cancellationToken: token);

        await Should.ThrowAsync<QueryNotSupportedException>(
            () => context.Orders.ToKeysetPageAsync(context, ByReferenceThenId, 5, page.Next, token));
    }

    [Fact]
    public async Task An_ordering_that_cannot_break_every_tie_is_refused_before_the_query_runs()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        var partial = KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt));

        await Should.ThrowAsync<QueryNotSupportedException>(
            () => context.Orders.ToKeysetPageAsync(context, partial, 5, cancellationToken: token));
    }

    [Fact]
    public async Task An_unconfigured_context_says_which_call_is_missing()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using var context = fixture.CreateContext(queryHelpers: false);

        var failure = await Should.ThrowAsync<QueryNotSupportedException>(
            () => context.Orders.ToKeysetPageAsync(
                context,
                ByPlacedThenId,
                5,
                cancellationToken: TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("UseQueryHelpers()");
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededAsync(
        int orderCount,
        Action<Configuration.QueryOptionsBuilder>? configure = null)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext(configure);

        if (orderCount > 0)
        {
            await Seed.OrdersAsync(context, orderCount, token);
        }

        context.ChangeTracker.Clear();
        return (context, token);
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededEmployeesAsync(int count)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext();

        await Seed.EmployeesAsync(context, count, token);

        context.ChangeTracker.Clear();
        return (context, token);
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededShipmentsAsync(int count)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext();

        await Seed.ShipmentsAsync(context, count, token);

        context.ChangeTracker.Clear();
        return (context, token);
    }
}
