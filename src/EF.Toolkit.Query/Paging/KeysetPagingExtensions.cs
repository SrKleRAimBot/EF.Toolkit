using EFToolkit.Query;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Paging;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Reads one page of a query by seeking past a cursor rather than counting from the start.</summary>
/// <remarks>
///     The right shape for infinite scroll, for APIs that hand out a "next" link, and for anything
///     deep enough that the offset itself becomes the cost. A page costs the same wherever it sits,
///     and a row inserted or removed elsewhere in the set cannot shift the boundary, so no row is
///     shown twice or skipped. In exchange there is no page number and no total: pages are reached
///     by walking, not by jumping.
/// </remarks>
public static class KeysetPagingExtensions
{
    /// <summary>Reads the page continuing from <paramref name="cursor" />.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">
    ///     The query to page. Do not order it — <paramref name="keys" /> supplies the whole ordering.
    ///     Any ordering already applied becomes a tiebreaker behind it, and since the definition ends
    ///     in a unique column it can never be reached.
    /// </param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="keys">The ordering to walk along.</param>
    /// <param name="pageSize">Rows per page, or <see langword="null" /> for the configured default.</param>
    /// <param name="cursor">
    ///     Where to continue from, or <see langword="null" /> to start at the first page.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The page, and the cursors reaching its neighbours.</returns>
    /// <exception cref="QueryNotSupportedException">
    ///     The ordering is not one a keyset page can be walked along correctly, or the cursor does not
    ///     belong to it.
    /// </exception>
    /// <example>
    ///     <code>
    ///     var page = await context.Orders
    ///         .Where(o => o.Total > 100)
    ///         .ToKeysetPageAsync(context, ByNewest, pageSize: 50, cursor);
    ///
    ///     return new { items = page.Items, next = page.Next?.Token };
    ///     </code>
    /// </example>
    public static Task<KeysetPage<T>> ToKeysetPageAsync<T>(
        this IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int? pageSize = null,
        KeysetCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(keys);

        if (pageSize is { } requested)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(requested, 1);
        }

        return PageAsync(source, context, keys, pageSize, cursor, cancellationToken);
    }

    /// <summary>Reads the page continuing from <paramref name="cursor" />, synchronously.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to page. Do not order it.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="keys">The ordering to walk along.</param>
    /// <param name="pageSize">Rows per page, or <see langword="null" /> for the configured default.</param>
    /// <param name="cursor">Where to continue from, or <see langword="null" /> to start at the first page.</param>
    /// <returns>The page, and the cursors reaching its neighbours.</returns>
    public static KeysetPage<T> ToKeysetPage<T>(
        this IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int? pageSize = null,
        KeysetCursor? cursor = null)
        => ToKeysetPageAsync(source, context, keys, pageSize, cursor).GetAwaiter().GetResult();

    private static async Task<KeysetPage<T>> PageAsync<T>(
        IQueryable<T> source,
        DbContext context,
        KeysetDefinition<T> keys,
        int? pageSize,
        KeysetCursor? cursor,
        CancellationToken cancellationToken)
    {
        var options = QueryConfiguration.Required(context, nameof(ToKeysetPageAsync));
        var size = Math.Min(pageSize ?? options.DefaultPageSize, options.MaxPageSize);

        keys.ValidateAgainst(context.Model.FindEntityType(typeof(T)));
        QueryAdvisor.InspectKeyset(context, source, keys, options);

        var backward = cursor is { Direction: KeysetPageDirection.Backward };

        IQueryable<T> query = keys.Order(source, backward);

        if (cursor is not null)
        {
            query = query.Where(keys.After(cursor));
        }

        // One row past the page: its existence is the answer to "is there more", and it costs a row
        // rather than the count of everything beyond the boundary.
        var rows = await query.Take(size + 1).ToListAsync(cancellationToken).ConfigureAwait(false);

        var hasExtra = rows.Count > size;
        if (hasExtra)
        {
            rows.RemoveAt(size);
        }

        if (backward)
        {
            // Read in reverse to seek backwards from the cursor; handed back in the definition's own
            // order, so a caller stepping back and forth sees one consistent sequence.
            rows.Reverse();
        }

        var hasNext = backward || hasExtra;
        var hasPrevious = backward ? hasExtra : cursor is not null;

        return new KeysetPage<T>(
            rows,
            size,
            next: hasNext && rows.Count > 0
                ? keys.CursorFor(rows[^1], KeysetPageDirection.Forward)
                : null,
            previous: hasPrevious && rows.Count > 0
                ? keys.CursorFor(rows[0], KeysetPageDirection.Backward)
                : null,
            hasNext,
            hasPrevious);
    }
}
