using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence;

/// <summary>
///     Deliberately breaks the paging walk and asserts the harness notices.
/// </summary>
/// <remarks>
///     A harness that silently compared nothing would look exactly like a harness that was passing.
///     Every check in <see cref="PagingEquivalence" /> is a claim that a particular kind of wrongness
///     would be caught; these are the tests that make those claims falsifiable.
/// </remarks>
public abstract class HarnessSelfTests(QueryDatabaseFixture fixture)
{
    private static readonly KeysetDefinition<Order> ByPlacedThenId = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    [Fact]
    public async Task A_walk_that_loses_rows_is_caught()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        // Stands in for the classic wrong keyset predicate — "a > a0 && b > b0" — which drops exactly
        // the rows that tie on the leading column.
        var failure = await Should.ThrowAsync<Xunit.Sdk.FailException>(
            () => PagingEquivalence.AssertAsync(
                context,
                context.Orders,
                ByPlacedThenId,
                static o => o.Id,
                pageSize: 5,
                token,
                sabotage: rows => rows.Where((_, i) => i % 3 != 0).ToList()));

        failure.Message.ShouldContain("never returned");
    }

    [Fact]
    public async Task A_walk_that_repeats_rows_is_caught()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var failure = await Should.ThrowAsync<Xunit.Sdk.FailException>(
            () => PagingEquivalence.AssertAsync(
                context,
                context.Orders,
                ByPlacedThenId,
                static o => o.Id,
                pageSize: 5,
                token,
                sabotage: rows => [.. rows, .. rows.Take(3)]));

        failure.Message.ShouldContain("more than once");
    }

    [Fact]
    public async Task A_walk_that_returns_the_right_rows_in_the_wrong_order_is_caught()
    {
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        var failure = await Should.ThrowAsync<Xunit.Sdk.FailException>(
            () => PagingEquivalence.AssertAsync(
                context,
                context.Orders,
                ByPlacedThenId,
                static o => o.Id,
                pageSize: 5,
                token,
                sabotage: rows =>
                {
                    var shuffled = rows.ToList();
                    (shuffled[0], shuffled[^1]) = (shuffled[^1], shuffled[0]);
                    return shuffled;
                }));

        failure.Message.ShouldContain("same rows, different order");
    }

    [Fact]
    public async Task An_intact_walk_passes()
    {
        // The control. Without it the three tests above would still pass if the harness failed
        // everything unconditionally.
        var (context, token) = await SeededAsync(30);
        await using var _ = context;

        await PagingEquivalence.AssertAsync(
            context, context.Orders, ByPlacedThenId, static o => o.Id, 5, token);
    }

    [Fact]
    public async Task The_fixture_really_resets_between_scenarios()
    {
        // Every suite here opens with a reset and then counts rows. If the reset were a no-op the
        // counts would drift upward as the suite ran, and the assertions that check exact counts
        // would fail in confusing places rather than here.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var token = TestContext.Current.CancellationToken;

        for (var round = 0; round < 2; round++)
        {
            await fixture.ResetAsync();

            await using var context = fixture.CreateContext();
            await Seed.OrdersAsync(context, 5, token);

            (await context.Orders.CountAsync(token)).ShouldBe(5);
        }
    }

    private async Task<(ShopContext Context, CancellationToken Token)> SeededAsync(int orderCount)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;
        var context = fixture.CreateContext();

        await Seed.OrdersAsync(context, orderCount, token);

        context.ChangeTracker.Clear();
        return (context, token);
    }
}
