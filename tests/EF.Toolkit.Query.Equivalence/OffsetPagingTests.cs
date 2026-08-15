using EFToolkit.Query.Configuration;
using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence;

/// <summary>Covers offset paging and the three ways it can report what lies beyond a page.</summary>
public abstract class OffsetPagingTests(QueryDatabaseFixture fixture)
{
    [Fact]
    public async Task A_full_walk_covers_every_row_once()
    {
        var (context, token) = await SeededAsync(23);
        await using var _ = context;

        var seen = new List<int>();

        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await Ordered(context)
                .ToPagedResultAsync(context, PageRequest.Of(pageNumber, 5), token);

            seen.AddRange(page.Items.Select(static o => o.Id));

            if (page.HasNext != true)
            {
                break;
            }
        }

        seen.Count.ShouldBe(23);
        seen.Distinct().Count().ShouldBe(23);
    }

    [Fact]
    public async Task TotalCount_reports_the_whole_set_and_the_page_count()
    {
        var (context, token) = await SeededAsync(23);
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 5), token);

        page.TotalCount.ShouldBe(23);
        page.TotalPages.ShouldBe(5);
        page.HasNext.ShouldBe(true);
        page.HasPrevious.ShouldBeFalse();
    }

    [Fact]
    public async Task TotalPages_is_exact_when_the_set_divides_evenly()
    {
        var (context, token) = await SeededAsync(20);
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 5), token);

        page.TotalPages.ShouldBe(4);
    }

    [Fact]
    public async Task HasNextProbe_answers_in_one_round_trip_and_leaves_the_total_unknown()
    {
        var (context, token) = await SeededAsync(23, q => q.CountStrategy(PageCountStrategy.HasNextProbe));
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 5), token);

        page.Items.Count.ShouldBe(5, "the probed row must be trimmed off the page");
        page.HasNext.ShouldBe(true);
        page.TotalCount.ShouldBeNull();
        page.TotalPages.ShouldBeNull();
    }

    [Fact]
    public async Task HasNextProbe_reports_no_next_page_on_the_last_one()
    {
        var (context, token) = await SeededAsync(10, q => q.CountStrategy(PageCountStrategy.HasNextProbe));
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(2, 5), token);

        page.Items.Count.ShouldBe(5);
        page.HasNext.ShouldBe(false);
    }

    [Fact]
    public async Task None_reports_neither_a_total_nor_whether_more_follows()
    {
        var (context, token) = await SeededAsync(23, q => q.CountStrategy(PageCountStrategy.None));
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 5), token);

        page.Items.Count.ShouldBe(5);
        page.TotalCount.ShouldBeNull();
        page.HasNext.ShouldBeNull();
    }

    [Fact]
    public async Task The_last_page_is_short_and_reports_nothing_after_it()
    {
        var (context, token) = await SeededAsync(23);
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(5, 5), token);

        page.Items.Count.ShouldBe(3);
        page.HasNext.ShouldBe(false);
        page.HasPrevious.ShouldBeTrue();
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_still_reports_the_total()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(99, 5), token);

        page.Items.ShouldBeEmpty();
        page.IsEmpty.ShouldBeTrue();
        page.HasNext.ShouldBe(false);
        page.TotalCount.ShouldBe(10);
    }

    [Fact]
    public async Task An_empty_set_reports_zero_pages()
    {
        var (context, token) = await SeededAsync(0);
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 5), token);

        page.Items.ShouldBeEmpty();
        page.TotalCount.ShouldBe(0);
        page.TotalPages.ShouldBe(0);
        page.HasNext.ShouldBe(false);
        page.HasPrevious.ShouldBeFalse();
    }

    [Fact]
    public async Task Zero_based_numbering_reads_the_same_rows_one_page_number_lower()
    {
        var (context, token) = await SeededAsync(20);
        await using var _ = context;

        var oneBased = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(2, 5), token);

        await using var zeroBasedContext = fixture.CreateContext(
            q => q.PageNumbering(PageNumbering.ZeroBased));

        var zeroBased = await Ordered(zeroBasedContext)
            .ToPagedResultAsync(zeroBasedContext, PageRequest.Of(1, 5), token);

        zeroBased.Items.Select(static o => o.Id).ShouldBe(oneBased.Items.Select(static o => o.Id));
    }

    [Fact]
    public async Task A_page_size_beyond_the_ceiling_is_clamped_rather_than_refused()
    {
        var (context, token) = await SeededAsync(20, q => q.DefaultPageSize(4).MaxPageSize(4));
        await using var _ = context;

        var page = await Ordered(context)
            .ToPagedResultAsync(context, PageRequest.Of(1, 1_000), token);

        page.PageSize.ShouldBe(4);
        page.Items.Count.ShouldBe(4);
    }

    [Fact]
    public async Task The_context_free_overload_works_without_UseQueryHelpers()
    {
        // The escape hatch for code that does not have the context to hand. It gets QueryOptions
        // defaults and no advisories, and must not demand configuration it cannot see.
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        var token = TestContext.Current.CancellationToken;

        await using var seeding = fixture.CreateContext();
        await Seed.OrdersAsync(seeding, 30, token);

        await using var context = fixture.CreateContext(queryHelpers: false);

        var page = await Ordered(context).ToPagedResultAsync(PageRequest.Of(2), token);

        page.PageSize.ShouldBe(QueryOptions.DefaultDefaultPageSize);
        page.Items.Count.ShouldBe(10);
        page.TotalCount.ShouldBe(30);
    }

    [Fact]
    public async Task The_context_overload_refuses_an_unconfigured_context()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await fixture.ResetAsync();

        await using var context = fixture.CreateContext(queryHelpers: false);

        await Should.ThrowAsync<QueryNotSupportedException>(
            () => Ordered(context).ToPagedResultAsync(
                context,
                PageRequest.Of(1),
                TestContext.Current.CancellationToken));
    }

    private static IQueryable<Order> Ordered(ShopContext context)
        => context.Orders.OrderBy(o => o.PlacedAt).ThenBy(o => o.Id);

    private async Task<(ShopContext Context, CancellationToken Token)> SeededAsync(
        int orderCount,
        Action<QueryOptionsBuilder>? configure = null)
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
}
