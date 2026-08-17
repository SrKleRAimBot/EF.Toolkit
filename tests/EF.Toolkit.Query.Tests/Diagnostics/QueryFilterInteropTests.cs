using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Sorting;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Diagnostics;

/// <summary>
///     How EF.Toolkit.Query meets EF Core's global query filters.
/// </summary>
/// <remarks>
///     <para>
///         There are two halves to this, and they need opposite things. The extensions themselves
///         compose onto the caller's <c>IQueryable</c>, so EF applies its filters underneath them
///         exactly as it would to any other query — the requirement there is that nothing this
///         library does may drop one, which the first group asserts against the generated SQL.
///     </para>
///     <para>
///         The advisor is the half that has to know. A filter is applied during translation and
///         never appears in the expression tree, so a probe that reads only the tree cannot see the
///         column a tenant filter pins — and that is precisely the column a well-chosen index leads
///         with. Left unfolded, the index-coverage check reported a missing index against queries
///         the existing index serves, which is the failure mode that teaches people to switch the
///         whole feature off.
///     </para>
/// </remarks>
public class QueryFilterInteropTests
{
    // ---------------------------------------------------------------------------------------
    // The extensions compose; EF's filters survive
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Sorting_leaves_a_global_query_filter_in_the_query()
    {
        using var context = Filtered();

        var sql = context.Orders
            .OrderBy(SortSpecification.For<Order>(s => s
                .Allow("total", o => o.Total)
                .DefaultOrder("total")
                .Tiebreaker(o => o.Id)))
            .ToQueryString();

        // The predicate, not the column: CustomerId is in the select list of every Order query, so
        // its bare name would pass whether or not the filter survived.
        sql.ShouldContain(FilterPredicate);
        sql.ShouldContain("ORDER BY");
    }

    [Fact]
    public void Filtering_helpers_leave_a_global_query_filter_in_the_query()
    {
        // WhereIf and friends return the source itself or a composed Where, so there is no path by
        // which they could reach past EF and drop a filter. Asserted anyway, because the cost of
        // being wrong about it is that a tenant sees another tenant's rows.
        using var context = Filtered();

        var sql = context.Orders
            .WhereIf(condition: true, o => o.Total > 100)
            .WhereIn(o => o.Status, [OrderStatus.Placed])
            .ToQueryString();

        sql.ShouldContain(FilterPredicate);
    }

    [Fact]
    public void An_explicit_IgnoreQueryFilters_is_left_alone()
    {
        // The caller's decision, not this library's. Composing on top of it must not put the filter
        // back any more than it may take one away.
        using var context = Filtered();

        var sql = context.Orders
            .IgnoreQueryFilters()
            .WhereIf(condition: true, o => o.Total > 100)
            .ToQueryString();

        sql.ShouldNotContain(FilterPredicate);
    }

    // ---------------------------------------------------------------------------------------
    // The advisor accounts for what the filter constrains
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_index_led_by_the_filter_column_silences_the_missing_index_advisory()
    {
        // The query orders by PlacedAt and says nothing about CustomerId; the filter pins it. The
        // index leads with the pinned column and then the ordering, which is exactly the prefix
        // that serves this query without a sort.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<Order>().HasQueryFilter(o => o.CustomerId == 1);
                b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id });
            });

        InspectFirstPage(context, context.Orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void The_same_query_without_the_filter_still_reports_the_missing_index()
    {
        // The negative control for the test above. Without the filter nothing pins CustomerId, the
        // index's leading column is unconstrained, and the finding is correct — so it must still
        // fire. Otherwise the previous test would pass for the wrong reason.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b => b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id }));

        InspectFirstPage(context, context.Orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Checks.ShouldContain(QueryChecks.MissingIndex);
    }

    [Fact]
    public void A_soft_delete_filter_counts_as_a_pinned_column()
    {
        // `e => !e.IsDeleted` is how a soft-delete filter is nearly always written. It pins the
        // column just as firmly as `== false` does, and an index that leads with it serves the
        // query just as well.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<SoftDeleted>().HasQueryFilter(e => !e.IsDeleted);
                b.Entity<SoftDeleted>().HasIndex(e => new { e.IsDeleted, e.Name, e.Id });
            });

        InspectFirstPage(
            context,
            context.Set<SoftDeleted>().OrderBy(e => e.Name).ThenBy(e => e.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_filter_the_query_ignored_is_not_credited()
    {
        // IgnoreQueryFilters means the filter is genuinely not in the executed query, so the column
        // is genuinely unconstrained. Crediting it anyway would replace a false positive with a
        // false negative — the worse of the two, because it hides a real missing index.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<Order>().HasQueryFilter(o => o.CustomerId == 1);
                b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id });
            });

        InspectFirstPage(
            context,
            context.Orders.IgnoreQueryFilters().OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Checks.ShouldContain(QueryChecks.MissingIndex);
    }

    [Fact]
    public void A_named_filter_the_query_ignored_by_name_is_not_credited()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<Order>().HasQueryFilter("tenant", o => o.CustomerId == 1);
                b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id });
            });

        InspectFirstPage(
            context,
            context.Orders
                .IgnoreQueryFilters(["tenant"])
                .OrderBy(o => o.PlacedAt)
                .ThenBy(o => o.Id));

        recorder.Checks.ShouldContain(QueryChecks.MissingIndex);
    }

    [Fact]
    public void A_named_filter_the_query_kept_is_still_credited()
    {
        // Ignoring one filter by name says nothing about the others. The edge that separates the
        // keyed overload from the blanket one.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<Order>().HasQueryFilter("tenant", o => o.CustomerId == 1);
                b.Entity<Order>().HasQueryFilter("live", o => o.Status == OrderStatus.Placed);
                b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id });
            });

        InspectFirstPage(
            context,
            context.Orders
                .IgnoreQueryFilters(["live"])
                .OrderBy(o => o.PlacedAt)
                .ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_keyset_page_accounts_for_the_filter_too()
    {
        // The keyset path takes its ordering from the definition rather than the tree, but reads
        // filters from the same shape — so it has to fold the query filter in as well.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b =>
            {
                b.Entity<Order>().HasQueryFilter(o => o.CustomerId == 1);
                b.Entity<Order>().HasIndex(o => new { o.CustomerId, o.PlacedAt, o.Id });
            });

        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt).Ascending(o => o.Id));

        QueryAdvisor.InspectKeyset(context, context.Orders, keys, context.Options());

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void An_entity_with_no_filter_is_unaffected()
    {
        // The fold-in must be a no-op where there is nothing to fold, or every finding in the suite
        // would be reachable through it.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            static b => b.Entity<Order>().HasIndex(o => new { o.PlacedAt, o.Id }));

        InspectFirstPage(context, context.Orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------------------------------
    // What the probe reads off the tree
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_probe_notices_a_blanket_IgnoreQueryFilters()
    {
        using var context = TestModel.Context();

        var shape = QueryShapeProbe.Inspect(context.Orders.IgnoreQueryFilters().Expression);

        shape.IgnoresAllQueryFilters.ShouldBeTrue();
        shape.IgnoredQueryFilterKeys.ShouldBeEmpty();
    }

    [Fact]
    public void The_probe_reads_the_names_off_a_keyed_IgnoreQueryFilters()
    {
        using var context = TestModel.Context();

        var shape = QueryShapeProbe.Inspect(
            context.Orders.IgnoreQueryFilters(["tenant", "live"]).Expression);

        shape.IgnoresAllQueryFilters.ShouldBeFalse();
        shape.IgnoredQueryFilterKeys.ShouldBe(["tenant", "live"], ignoreOrder: true);
    }

    [Fact]
    public void The_probe_reports_no_ignored_filters_for_an_ordinary_query()
    {
        using var context = TestModel.Context();

        var shape = QueryShapeProbe.Inspect(context.Orders.AsQueryable().Expression);

        shape.IgnoresAllQueryFilters.ShouldBeFalse();
        shape.IgnoredQueryFilterKeys.ShouldBeEmpty();
    }

    [Fact]
    public void A_negated_boolean_in_a_Where_is_a_pinned_column()
    {
        using var context = TestModel.Context(onModelCreating: static b => b.Entity<SoftDeleted>());

        var shape = QueryShapeProbe.Inspect(
            context.Set<SoftDeleted>().Where(e => !e.IsDeleted).Expression);

        shape.EqualityPaths.ShouldBe([nameof(SoftDeleted.IsDeleted)]);
    }

    [Fact]
    public void A_bare_boolean_in_a_Where_is_a_pinned_column()
    {
        using var context = TestModel.Context(onModelCreating: static b => b.Entity<SoftDeleted>());

        var shape = QueryShapeProbe.Inspect(
            context.Set<SoftDeleted>().Where(e => e.IsDeleted).Expression);

        shape.EqualityPaths.ShouldBe([nameof(SoftDeleted.IsDeleted)]);
    }

    /// <summary>How SQL Server renders the filter <see cref="Filtered" /> declares.</summary>
    private const string FilterPredicate = "[CustomerId] = 1";

    private static QueryTestContext Filtered()
        => TestModel.Context(
            onModelCreating: static b => b.Entity<Order>().HasQueryFilter(o => o.CustomerId == 1));

    private static QueryTestContext Diagnosing(Action<ModelBuilder> onModelCreating)
        => TestModel.Context(
            q => q.Diagnostics(d => d
                .WarnOnMissingIndex()
                .OnWarning(QueryWarningBehavior.Diagnostic)),
            onModelCreating);

    private static void InspectFirstPage<T>(QueryTestContext context, IQueryable<T> query)
        => QueryAdvisor.InspectPage(
            context,
            query,
            new ResolvedPage(1, 20, 0, WasClamped: false),
            context.Options());
}

/// <summary>Stands in for the soft-delete flag a query filter most often keys on.</summary>
public class SoftDeleted
{
    public int Id { get; set; }

    public bool IsDeleted { get; set; }

    public string Name { get; set; } = "";
}
