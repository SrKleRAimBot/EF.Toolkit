using EFToolkit.Query.Equivalence.Infrastructure;
using EFToolkit.Query.Equivalence.Model;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence;

/// <summary>Covers the batched walk over a whole result set.</summary>
public abstract class StreamingTests(QueryDatabaseFixture fixture)
{
    private static readonly KeysetDefinition<Order> ById = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.Id));

    private static readonly KeysetDefinition<Order> ByPlacedThenId = KeysetDefinition.For<Order>(k => k
        .Ascending(o => o.PlacedAt)
        .Ascending(o => o.Id));

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    [InlineData(500)]
    public async Task Every_row_is_visited_exactly_once_whatever_the_batch_size(int batchSize)
    {
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        var seen = new List<int>();

        await foreach (var row in context.Orders.StreamAsync(context, ById, batchSize, token))
        {
            seen.Add(row.Id);
        }

        seen.Count.ShouldBe(40);
        seen.Distinct().Count().ShouldBe(40);
        seen.ShouldBeInOrder();
    }

    [Fact]
    public async Task Batches_come_back_full_until_the_last_one()
    {
        var (context, token) = await SeededAsync(23);
        await using var _ = context;

        var sizes = new List<int>();

        await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, 5, token))
        {
            sizes.Add(batch.Count);
        }

        sizes.ShouldBe([5, 5, 5, 5, 3]);
    }

    [Fact]
    public async Task An_empty_set_yields_no_batches()
    {
        var (context, token) = await SeededAsync(0);
        await using var _ = context;

        var batches = 0;

        await foreach (var _batch in context.Orders.StreamBatchesAsync(context, ById, 5, token))
        {
            batches++;
        }

        batches.ShouldBe(0);
    }

    [Fact]
    public async Task A_set_that_fits_in_one_batch_yields_exactly_one()
    {
        var (context, token) = await SeededAsync(5);
        await using var _ = context;

        var batches = new List<int>();

        await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, 10, token))
        {
            batches.Add(batch.Count);
        }

        batches.ShouldBe([5]);
    }

    [Fact]
    public async Task A_set_exactly_filling_one_batch_does_not_yield_an_empty_second()
    {
        var (context, token) = await SeededAsync(10);
        await using var _ = context;

        var batches = new List<int>();

        await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, 10, token))
        {
            batches.Add(batch.Count);
        }

        batches.ShouldBe([10]);
    }

    [Fact]
    public async Task A_filtered_stream_visits_only_the_matching_rows()
    {
        var (context, token) = await SeededAsync(40);
        await using var _ = context;

        var expected = await context.Orders
            .Where(o => o.Status == OrderStatus.Shipped)
            .CountAsync(token);

        var seen = 0;

        await foreach (var row in context.Orders
            .Where(o => o.Status == OrderStatus.Shipped)
            .StreamAsync(context, ByPlacedThenId, 4, token))
        {
            row.Status.ShouldBe(OrderStatus.Shipped);
            seen++;
        }

        expected.ShouldBeGreaterThan(0);
        seen.ShouldBe(expected);
    }

    [Fact]
    public async Task Cancelling_mid_stream_stops_promptly()
    {
        var (context, _) = await SeededAsync(40);
        await using var __ = context;

        using var cts = new CancellationTokenSource();
        var seen = 0;

        var failure = await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in context.Orders.StreamAsync(context, ById, 5, cts.Token))
            {
                seen++;

                if (seen == 7)
                {
                    await cts.CancelAsync();
                }
            }
        });

        failure.ShouldNotBeNull();

        // Stopped inside the batch it was in rather than reading the rest of the table.
        seen.ShouldBeLessThan(40);
    }

    [Fact]
    public async Task An_unset_batch_size_uses_the_configured_default()
    {
        var (context, token) = await SeededAsync(25, q => q.BatchSize(6));
        await using var _ = context;

        var sizes = new List<int>();

        await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, cancellationToken: token))
        {
            sizes.Add(batch.Count);
        }

        sizes.ShouldBe([6, 6, 6, 6, 1]);
    }

    [Fact]
    public async Task A_non_positive_batch_size_is_refused()
    {
        var (context, token) = await SeededAsync(5);
        await using var _ = context;

        Should.Throw<ArgumentOutOfRangeException>(
            () => context.Orders.StreamBatchesAsync(context, ById, 0, token));
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
}
