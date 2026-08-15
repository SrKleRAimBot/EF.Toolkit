using EFToolkit.Query.Tests.Infrastructure;
using EFToolkit.Query.Tracking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Query.Tests.Tracking;

/// <summary>
///     Covers the ambient scope's nesting and isolation. What the scope does to a real query is the
///     equivalence suite's job; this pins the bookkeeping.
/// </summary>
public class QueryTrackingScopeTests
{
    [Fact]
    public void With_no_scope_there_is_no_ambient_preference()
        => QueryTracking.Current.ShouldBeNull();

    [Fact]
    public void A_scope_is_visible_while_it_is_alive_and_gone_afterwards()
    {
        using (QueryTracking.NoTracking())
        {
            QueryTracking.Current.ShouldBe(QueryTrackingBehavior.NoTracking);
        }

        QueryTracking.Current.ShouldBeNull();
    }

    [Fact]
    public void The_innermost_scope_wins_and_disposing_restores_the_outer_one()
    {
        // Popping rather than clearing is the whole point: a nested scope that cleared on the way out
        // would silently drop the caller back to tracking in the middle of a no-tracking block.
        using (QueryTracking.NoTracking())
        {
            using (QueryTracking.Tracking())
            {
                using (QueryTracking.NoTrackingWithIdentityResolution())
                {
                    QueryTracking.Current
                        .ShouldBe(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
                }

                QueryTracking.Current.ShouldBe(QueryTrackingBehavior.TrackAll);
            }

            QueryTracking.Current.ShouldBe(QueryTrackingBehavior.NoTracking);
        }

        QueryTracking.Current.ShouldBeNull();
    }

    [Fact]
    public void Disposing_twice_does_nothing_the_second_time()
    {
        var outer = QueryTracking.NoTracking();
        var inner = QueryTracking.Tracking();

        inner.Dispose();
        inner.Dispose();

        QueryTracking.Current.ShouldBe(QueryTrackingBehavior.NoTracking);

        outer.Dispose();
        QueryTracking.Current.ShouldBeNull();
    }

    [Fact]
    public void Disposing_out_of_order_neither_drops_a_live_scope_nor_resurrects_a_dead_one()
    {
        var outer = QueryTracking.NoTracking();
        var inner = QueryTracking.Tracking();

        // Releasing the outer scope early must not take the inner one with it — the code inside the
        // inner using block asked for tracking and has not finished.
        outer.Dispose();
        QueryTracking.Current.ShouldBe(QueryTrackingBehavior.TrackAll);

        // Nor may closing the inner one hand control back to a scope that has already been disposed.
        inner.Dispose();
        QueryTracking.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Sibling_flows_do_not_see_each_other_s_scopes()
    {
        // The scope rides an AsyncLocal, so two requests handled in parallel must not be able to
        // change each other's tracking. Without the isolation this is a cross-request data bug.
        var cancellationToken = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource();
        var observed = new TaskCompletionSource<QueryTrackingBehavior?>();

        var other = Task.Run(
            async () =>
            {
                await started.Task;
                return QueryTracking.Current;
            },
            cancellationToken);

        var mine = Task.Run(
            async () =>
            {
                using (QueryTracking.NoTracking())
                {
                    started.SetResult();
                    observed.SetResult(QueryTracking.Current);
                    await other;
                }
            },
            cancellationToken);

        (await observed.Task).ShouldBe(QueryTrackingBehavior.NoTracking);
        (await other).ShouldBeNull();
        await mine;
    }

    [Fact]
    public async Task A_scope_flows_into_work_started_inside_it()
    {
        using (QueryTracking.NoTracking())
        {
            var inherited = await Task.Run(static () => QueryTracking.Current, TestContext.Current.CancellationToken);
            inherited.ShouldBe(QueryTrackingBehavior.NoTracking);
        }
    }

    [Fact]
    public void The_per_context_scope_restores_the_previous_preference()
    {
        using var context = TestModel.Context();
        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        using (context.BeginTrackingScope(QueryTrackingBehavior.TrackAll))
        {
            context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.TrackAll);
        }

        // Restored to what the application had chosen, not to the provider default.
        context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
    }

    [Fact]
    public void The_per_context_scope_nests()
    {
        using var context = TestModel.Context();

        using (context.BeginTrackingScope(QueryTrackingBehavior.NoTracking))
        {
            using (context.BeginTrackingScope(QueryTrackingBehavior.NoTrackingWithIdentityResolution))
            {
                context.ChangeTracker.QueryTrackingBehavior
                    .ShouldBe(QueryTrackingBehavior.NoTrackingWithIdentityResolution);
            }

            context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.NoTracking);
        }

        context.ChangeTracker.QueryTrackingBehavior.ShouldBe(QueryTrackingBehavior.TrackAll);
    }

    [Fact]
    public void The_per_context_scope_rejects_a_null_context()
        => Should.Throw<ArgumentNullException>(
            () => DbContextTrackingExtensions.BeginTrackingScope(null!, QueryTrackingBehavior.NoTracking));

    [Fact]
    public void Configuring_the_context_replaces_the_query_context_factory()
    {
        // The service replacement is what carries the ambient scope into EF's compilation path, on the
        // one seam neither provider overrides. If it stops being applied, scopes silently do nothing.
        using var context = TestModel.Context();

        context.GetService<Microsoft.EntityFrameworkCore.Query.IQueryContextFactory>()
            .ShouldBeOfType<TrackingScopeQueryContextFactory>();
    }

    [Fact]
    public void Turning_tracking_scopes_off_leaves_EF_s_own_factory_in_place()
    {
        using var context = TestModel.Context(q => q.WithoutTrackingScopes());

        context.GetService<Microsoft.EntityFrameworkCore.Query.IQueryContextFactory>()
            .ShouldNotBeOfType<TrackingScopeQueryContextFactory>();
    }
}
