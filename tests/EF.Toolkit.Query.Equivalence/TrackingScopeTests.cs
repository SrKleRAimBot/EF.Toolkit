using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Tracking;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence;

/// <summary>
///     Covers what an ambient tracking scope does to a real query on a real engine — including the
///     compiled-query cache, which is the reason the scope is applied where it is.
/// </summary>
public abstract class TrackingScopeTests(QueryDatabaseFixture fixture)
{
    [Fact]
    public async Task Queries_inside_a_no_tracking_scope_are_not_tracked()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().ShouldBeEmpty();
    }

    [Fact]
    public async Task Queries_outside_the_scope_are_tracked_again()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().ShouldBeEmpty();

        await context.Orders.ToListAsync(token);
        context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
    }

    [Fact]
    public async Task The_compiled_query_cache_does_not_serve_a_tracked_plan_inside_a_no_tracking_scope()
    {
        // The single most important test in the package. EF caches compiled queries, and a scope
        // applied anywhere after the cache key is computed would be baked into the first compilation
        // and silently ignored on every later execution of the same LINQ.
        //
        // Run tracked first so the cache is populated with the tracking plan, then run the identical
        // query inside a no-tracking scope on the same context.
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        await context.Orders.OrderBy(o => o.Id).ToListAsync(token);
        context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);

        context.ChangeTracker.Clear();

        using (QueryTracking.NoTracking())
        {
            await context.Orders.OrderBy(o => o.Id).ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>()
            .ShouldBeEmpty("the cached tracking plan must not be reused inside a no-tracking scope");
    }

    [Fact]
    public async Task The_cache_serves_the_tracking_plan_again_after_the_scope_closes()
    {
        // The other half of the same hazard: having compiled a no-tracking plan, the tracked
        // execution that follows must not be served from it.
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.OrderBy(o => o.Id).ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().ShouldBeEmpty();

        await context.Orders.OrderBy(o => o.Id).ToListAsync(token);
        context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
    }

    [Fact]
    public async Task An_inner_tracking_scope_wins_over_an_outer_no_tracking_one()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.ToListAsync(token);
            context.ChangeTracker.Entries<Order>().ShouldBeEmpty();

            using (QueryTracking.Tracking())
            {
                await context.Orders.ToListAsync(token);
                context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
            }

            context.ChangeTracker.Clear();
            await context.Orders.ToListAsync(token);
            context.ChangeTracker.Entries<Order>().ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task An_explicit_AsTracking_beats_the_scope()
    {
        // EF applies the operator it finds in the expression tree over the context-level preference
        // the scope sets. Documented precedence, so it is pinned.
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.AsTracking().ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
    }

    [Fact]
    public async Task An_explicit_AsNoTracking_still_wins_inside_a_tracking_scope()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.Tracking())
        {
            await context.Orders.AsNoTracking().ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_scope_restores_a_preference_the_application_set_by_hand()
    {
        // The factory must put back what the context had, not the provider default — otherwise
        // closing a scope silently switches a deliberately no-tracking context back to tracking.
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

        using (QueryTracking.Tracking())
        {
            await context.Orders.ToListAsync(token);
            context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
        }

        context.ChangeTracker.Clear();
        await context.Orders.ToListAsync(token);

        context.ChangeTracker.Entries<Order>()
            .ShouldBeEmpty("the context's own NoTracking preference must survive the scope");
    }

    [Fact]
    public async Task Identity_resolution_scopes_return_one_instance_per_row()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (QueryTracking.NoTrackingWithIdentityResolution())
        {
            var rows = await context.Orders
                .Include(o => o.Customer)
                .ToListAsync(token);

            context.ChangeTracker.Entries<Order>().ShouldBeEmpty();

            // Four customers across ten orders, and identity resolution means the orders sharing one
            // customer share the instance rather than each getting a copy.
            rows.Where(o => o.Customer is not null)
                .Select(o => o.Customer!)
                .Distinct()
                .Count()
                .ShouldBeLessThanOrEqualTo(4);
        }
    }

    [Fact]
    public async Task A_context_without_tracking_scopes_ignores_the_ambient_preference()
    {
        var (context, token) = await SeededAsync(10, q => q.WithoutTrackingScopes());
        await using var _ = context;

        using (QueryTracking.NoTracking())
        {
            await context.Orders.ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>()
            .Count().ShouldBe(10, "WithoutTrackingScopes leaves EF's own factory in place");
    }

    [Fact]
    public async Task The_per_context_scope_works_without_any_ambient_scope()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        using (context.BeginTrackingScope(QueryTrackingBehavior.NoTracking))
        {
            await context.Orders.ToListAsync(token);
        }

        context.ChangeTracker.Entries<Order>().ShouldBeEmpty();

        await context.Orders.ToListAsync(token);
        context.ChangeTracker.Entries<Order>().Count().ShouldBe(10);
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededAsync(
        int orderCount,
        Action<Configuration.QueryOptionsBuilder>? configure = null)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;

        await using (var seeding = fixture.CreateContext())
        {
            if (orderCount > 0)
            {
                await Seed.OrdersAsync(seeding, orderCount, token);
            }
        }

        return (fixture.CreateContext(configure), token);
    }
}
