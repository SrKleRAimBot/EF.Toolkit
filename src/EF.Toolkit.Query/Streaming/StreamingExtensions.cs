using System.Runtime.CompilerServices;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Paging;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Walks a whole result set in batches, without holding it all in memory.</summary>
/// <remarks>
///     <para>
///         Driven by keyset pagination rather than <c>Skip</c>/<c>Take</c>, so every batch costs the
///         same however deep into the set it sits, and a row inserted or deleted while the walk is in
///         progress cannot shift the boundary and cause a row to be visited twice or skipped.
///     </para>
///     <para>
///         Each batch is a separate query, so the walk is <em>not</em> a snapshot: rows committed
///         after it started, and after the boundary, are visited. Run it inside a transaction with a
///         repeatable-read or snapshot isolation level if that matters.
///     </para>
/// </remarks>
public static class StreamingExtensions
{
    /// <summary>Walks the query one batch at a time.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to walk. Do not order it — <paramref name="keys" /> does.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="keys">The ordering to walk along.</param>
    /// <param name="batchSize">Rows per batch, or <see langword="null" /> for the configured default.</param>
    /// <param name="cancellationToken">Cancels the walk between and during batches.</param>
    /// <returns>The batches, in the definition's order. The final batch may be short.</returns>
    /// <example>
    ///     <code>
    ///     await foreach (var batch in context.Orders.StreamBatchesAsync(context, ById, 1000, ct))
    ///     {
    ///         await context.BulkUpdateAsync(Recalculate(batch), cancellationToken: ct);
    ///     }
    ///     </code>
    /// </example>
    /// <remarks>
    ///     Validation happens here rather than in the iterator below. An <c>async
    ///     IAsyncEnumerable</c> method runs no code at all until something enumerates it, so a guard
    ///     written inside one fires late — at the <c>await foreach</c> rather than at the call that
    ///     passed the bad argument, with a stack trace pointing at the wrong line.
    /// </remarks>
    public static IAsyncEnumerable<IReadOnlyList<T>> StreamBatchesAsync<T>(
        this IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int? batchSize = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keys);

        if (batchSize is { } requested)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1);
        }

        var options = QueryConfiguration.Required(context, nameof(StreamBatchesAsync));

        return BatchesAsync(source, context, keys, batchSize ?? options.BatchSize, cancellationToken);
    }

    private static async IAsyncEnumerable<IReadOnlyList<T>> BatchesAsync<T>(
        IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int size,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        KeysetCursor? cursor = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await source
                .ToKeysetPageAsync(context, keys, size, cursor, cancellationToken)
                .ConfigureAwait(false);

            if (page.Items.Count > 0)
            {
                yield return page.Items;
            }

            // Next is null exactly when there is no following page, or when this one came back empty
            // and there is no row left to anchor a cursor to. Either way the walk is done.
            if (!page.HasNext || page.Next is null)
            {
                yield break;
            }

            cursor = page.Next;
        }
    }

    /// <summary>Walks the query one row at a time, reading a batch at a time underneath.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to walk. Do not order it — <paramref name="keys" /> does.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="keys">The ordering to walk along.</param>
    /// <param name="batchSize">
    ///     Rows fetched per round trip, or <see langword="null" /> for the configured default.
    /// </param>
    /// <param name="cancellationToken">Cancels the walk between and during batches.</param>
    /// <returns>Every matching row, in the definition's order.</returns>
    /// <remarks>
    ///     As with <see cref="StreamBatchesAsync" />, the arguments are checked before any iterator is
    ///     returned, so a bad one fails at the call rather than at the <c>await foreach</c>.
    /// </remarks>
    public static IAsyncEnumerable<T> StreamAsync<T>(
        this IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int? batchSize = null,
        CancellationToken cancellationToken = default)
        => RowsAsync(
            source.StreamBatchesAsync(context, keys, batchSize, cancellationToken),
            cancellationToken);

    private static async IAsyncEnumerable<T> RowsAsync<T>(
        IAsyncEnumerable<IReadOnlyList<T>> batches,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var batch in batches.ConfigureAwait(false))
        {
            foreach (var row in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
            }
        }
    }
}
