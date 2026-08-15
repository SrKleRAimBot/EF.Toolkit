using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Diagnostics;

/// <summary>Covers which advisories fire, and — just as importantly — which do not.</summary>
public class QueryAdvisorTests
{
    [Fact]
    public void With_diagnostics_off_nothing_is_inspected_at_all()
    {
        // The default. An application that leaves diagnostics alone must pay nothing for the feature
        // existing, so the checks have to be skipped rather than run and their findings discarded.
        using var recorder = new AdvisoryRecorder();
        using var context = TestModel.Context();

        QueryAdvisor.InspectPage(
            context,
            context.Orders,
            new ResolvedPage(1_000_000, 100, 99_999_900, WasClamped: false),
            context.Options());

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void Selecting_checks_without_a_behavior_still_reports_nothing()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = TestModel.Context(q => q.Diagnostics(d => d.WarnOnEverything()));

        QueryAdvisor.InspectPage(
            context,
            context.Orders,
            new ResolvedPage(1_000, 100, 99_900, WasClamped: false),
            context.Options());

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_deep_offset_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnDeepOffset(), q => q.MaxOffsetRows(1_000));

        QueryAdvisor.InspectPage(
            context,
            context.Orders,
            new ResolvedPage(100, 100, 9_900, WasClamped: false),
            context.Options());

        recorder.Checks.ShouldContain(QueryChecks.DeepOffset);
        recorder.Advisories.Single().Message.ShouldContain("ToKeysetPageAsync");
    }

    [Fact]
    public void An_offset_inside_the_threshold_is_not_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnDeepOffset(), q => q.MaxOffsetRows(1_000));

        QueryAdvisor.InspectPage(
            context,
            context.Orders,
            new ResolvedPage(2, 100, 100, WasClamped: false),
            context.Options());

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void An_unordered_paginated_query_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnNonDeterministicOrder());

        InspectFirstPage(context, context.Orders);

        recorder.Checks.ShouldContain(QueryChecks.NonDeterministicOrder);
        recorder.Advisories.Single().Message.ShouldContain("not ordered");
    }

    [Fact]
    public void An_ordering_that_cannot_break_every_tie_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnNonDeterministicOrder());

        InspectFirstPage(context, context.Orders.OrderBy(o => o.PlacedAt));

        recorder.Checks.ShouldContain(QueryChecks.NonDeterministicOrder);
        recorder.Advisories.Single().Message.ShouldContain("tie");
    }

    [Fact]
    public void An_ordering_ending_in_the_key_is_not_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnNonDeterministicOrder());

        InspectFirstPage(context, context.Orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_index_is_reported_and_the_message_names_the_fix()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnMissingIndex());

        InspectFirstPage(context, context.Orders.OrderBy(o => o.Total).ThenBy(o => o.Id));

        recorder.Checks.ShouldContain(QueryChecks.MissingIndex);

        var message = recorder.Advisories.Single().Message;
        message.ShouldContain("HasIndex");
        message.ShouldContain(nameof(Order.Total));
        message.ShouldContain("outside the EF model");
    }

    [Fact]
    public void A_covering_index_silences_the_missing_index_advisory()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            d => d.WarnOnMissingIndex(),
            onModelCreating: static b => b.Entity<Order>().HasIndex(x => new { x.Total, x.Id }));

        InspectFirstPage(context, context.Orders.OrderBy(o => o.Total).ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_filter_and_ordering_covered_as_a_prefix_is_not_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            d => d.WarnOnMissingIndex(),
            onModelCreating: static b =>
                b.Entity<Order>().HasIndex(x => new { x.CustomerId, x.PlacedAt, x.Id }));

        InspectFirstPage(
            context,
            context.Orders.Where(o => o.CustomerId == 1).OrderBy(o => o.PlacedAt).ThenBy(o => o.Id));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void Returning_whole_entities_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnEntityProjection());

        InspectFirstPage(context, context.Orders);

        recorder.Checks.ShouldContain(QueryChecks.EntityProjection);
    }

    [Fact]
    public void A_projection_is_not_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnEntityProjection());

        InspectFirstPage(
            context,
            context.Orders.Select(o => new OrderSummary { Id = o.Id, Total = o.Total }));

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void A_collection_include_under_a_single_query_page_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnCollectionIncludeWithPaging());

        InspectFirstPage(context, context.Customers.Include(c => c.Orders));

        recorder.Checks.ShouldContain(QueryChecks.CollectionIncludeWithPaging);
        recorder.Advisories.Single().Message.ShouldContain("AsSplitQuery");
    }

    [Fact]
    public void AsSplitQuery_silences_the_collection_include_advisory()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnCollectionIncludeWithPaging());

        InspectFirstPage(context, context.Customers.Include(c => c.Orders).AsSplitQuery());

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void An_oversized_in_clause_is_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnLargeInClause(), q => q.MaxInClauseValues(10));

        _ = context.Orders.WhereIn(context, o => o.Id, Enumerable.Range(1, 50)).ToString();

        recorder.Checks.ShouldContain(QueryChecks.LargeInClause);
        recorder.Advisories.Single().Message.ShouldContain("2100 parameters");
    }

    [Fact]
    public void An_in_clause_inside_the_threshold_is_not_reported()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnLargeInClause(), q => q.MaxInClauseValues(100));

        _ = context.Orders.WhereIn(context, o => o.Id, Enumerable.Range(1, 50)).ToString();

        recorder.Advisories.ShouldBeEmpty();
    }

    [Fact]
    public void Configured_to_throw_it_reports_every_finding_at_once()
    {
        // Fixing one finding and rerunning to discover the next turns a single review into several.
        using var context = TestModel.Context(q => q
            .Diagnostics(d => d.WarnOnEverything().OnWarning(QueryWarningBehavior.Throw)));

        var failure = Should.Throw<QueryNotSupportedException>(
            () => InspectFirstPage(context, context.Orders.OrderBy(o => o.Total)));

        failure.Message.ShouldContain(nameof(QueryChecks.NonDeterministicOrder));
        failure.Message.ShouldContain(nameof(QueryChecks.MissingIndex));
        failure.Message.ShouldContain(nameof(QueryChecks.EntityProjection));
    }

    [Fact]
    public void Configured_to_report_it_does_not_throw()
    {
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(d => d.WarnOnEverything());

        Should.NotThrow(() => InspectFirstPage(context, context.Orders));
        recorder.Advisories.ShouldNotBeEmpty();
    }

    [Fact]
    public void A_keyset_query_takes_its_ordering_from_the_definition_rather_than_the_tree()
    {
        // A keyset query is handed in unordered, so reading the ordering off the expression tree would
        // report every one of them as unindexed.
        using var recorder = new AdvisoryRecorder();
        using var context = Diagnosing(
            d => d.WarnOnMissingIndex(),
            onModelCreating: static b => b.Entity<Order>().HasIndex(x => new { x.PlacedAt, x.Id }));

        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt).Ascending(o => o.Id));

        QueryAdvisor.InspectKeyset(context, context.Orders, keys, context.Options());

        recorder.Advisories.ShouldBeEmpty();
    }

    private static QueryTestContext Diagnosing(
        Action<QueryDiagnosticsBuilder> diagnostics,
        Action<QueryOptionsBuilder>? options = null,
        Action<ModelBuilder>? onModelCreating = null)
        => TestModel.Context(
            q =>
            {
                options?.Invoke(q);
                q.Diagnostics(d =>
                {
                    diagnostics(d);
                    d.OnWarning(QueryWarningBehavior.Diagnostic);
                });
            },
            onModelCreating);

    private static void InspectFirstPage<T>(QueryTestContext context, IQueryable<T> query)
        => QueryAdvisor.InspectPage(
            context,
            query,
            new ResolvedPage(1, 20, 0, WasClamped: false),
            context.Options());
}
