using EFToolkit.Query;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Tests.Infrastructure;

namespace EFToolkit.Query.Tests.Configuration;

/// <summary>Covers the fluent configuration surface and what it refuses.</summary>
public class QueryOptionsBuilderTests
{
    [Fact]
    public void Defaults_are_the_documented_ones()
    {
        var options = QueryOptions.Default;

        options.DefaultPageSize.ShouldBe(20);
        options.MaxPageSize.ShouldBe(100);
        options.Numbering.ShouldBe(PageNumbering.OneBased);
        options.CountStrategy.ShouldBe(PageCountStrategy.TotalCount);
        options.TrackingScopes.ShouldBeTrue();
        options.Diagnostics.Checks.ShouldBe(QueryChecks.None);
        options.Diagnostics.Behavior.ShouldBe(QueryWarningBehavior.Ignore);
    }

    [Fact]
    public void Every_knob_round_trips_through_the_builder()
    {
        using var context = TestModel.Context(q => q
            .DefaultPageSize(25)
            .MaxPageSize(200)
            .PageNumbering(PageNumbering.ZeroBased)
            .CountStrategy(PageCountStrategy.HasNextProbe)
            .MaxOffsetRows(1_234)
            .BatchSize(500)
            .MaxInClauseValues(99)
            .WithoutTrackingScopes());

        var options = context.Options();

        options.DefaultPageSize.ShouldBe(25);
        options.MaxPageSize.ShouldBe(200);
        options.Numbering.ShouldBe(PageNumbering.ZeroBased);
        options.CountStrategy.ShouldBe(PageCountStrategy.HasNextProbe);
        options.MaxOffsetRows.ShouldBe(1_234);
        options.BatchSize.ShouldBe(500);
        options.MaxInClauseValues.ShouldBe(99);
        options.TrackingScopes.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Builder_rejects_non_positive_sizes(int value)
    {
        var builder = new QueryOptionsBuilder(QueryOptions.Default);

        Should.Throw<ArgumentOutOfRangeException>(() => builder.DefaultPageSize(value));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.MaxPageSize(value));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.MaxOffsetRows(value));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.BatchSize(value));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.MaxInClauseValues(value));
    }

    [Fact]
    public void Diagnostics_stay_off_when_checks_are_selected_but_no_behavior_is()
    {
        // Selecting checks is half the switch. Reporting nothing until someone asks for it is what
        // keeps an accidentally-enabled check from costing a production query anything.
        var builder = new QueryOptionsBuilder(QueryOptions.Default);
        builder.Diagnostics(d => d.WarnOnMissingIndex().WarnOnDeepOffset());

        builder.Options.Diagnostics.Checks.ShouldBe(QueryChecks.MissingIndex | QueryChecks.DeepOffset);
        builder.Options.Diagnostics.IsEnabled.ShouldBeFalse();
        builder.Options.Diagnostics.Runs(QueryChecks.MissingIndex).ShouldBeFalse();
    }

    [Fact]
    public void Diagnostics_run_once_a_behavior_is_chosen()
    {
        var builder = new QueryOptionsBuilder(QueryOptions.Default);
        builder.Diagnostics(d => d.WarnOnMissingIndex().OnWarning(QueryWarningBehavior.Diagnostic));

        builder.Options.Diagnostics.IsEnabled.ShouldBeTrue();
        builder.Options.Diagnostics.Runs(QueryChecks.MissingIndex).ShouldBeTrue();
        builder.Options.Diagnostics.Runs(QueryChecks.DeepOffset).ShouldBeFalse();
    }

    [Fact]
    public void WarnOnEverything_selects_every_check()
    {
        var builder = new QueryOptionsBuilder(QueryOptions.Default);
        builder.Diagnostics(d => d.WarnOnEverything().OnWarning(QueryWarningBehavior.Throw));

        foreach (var check in Enum.GetValues<QueryChecks>())
        {
            if (check is QueryChecks.None or QueryChecks.All)
            {
                continue;
            }

            builder.Options.Diagnostics.Runs(check).ShouldBeTrue($"{check} should be selected");
        }
    }

    [Fact]
    public void A_default_page_size_above_the_ceiling_is_refused_rather_than_silently_clamped()
    {
        // Left alone this configuration is self-contradicting: every request that named no size would
        // be clamped to the ceiling, so the configured default would never reach a single caller.
        var failure = Should.Throw<QueryNotSupportedException>(
            () => TestModel.Context(q => q.MaxPageSize(10).DefaultPageSize(50)).Options());

        failure.Message.ShouldContain("DefaultPageSize");
        failure.Message.ShouldContain("MaxPageSize");
    }

    [Fact]
    public void An_unconfigured_context_says_which_call_is_missing()
    {
        using var context = TestModel.Context(useQueryHelpers: false);

        var failure = Should.Throw<QueryNotSupportedException>(
            () => QueryConfiguration.Required(context, "ToPagedResultAsync"));

        failure.Message.ShouldContain("UseQueryHelpers()");
        failure.Message.ShouldContain("ToPagedResultAsync");
    }

    [Fact]
    public void Diagnostics_rejects_a_null_configuration()
        => Should.Throw<ArgumentNullException>(
            () => new QueryOptionsBuilder(QueryOptions.Default).Diagnostics(null!));
}
