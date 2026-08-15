using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Paging;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Reads one page of a query by offset.</summary>
/// <remarks>
///     Offset pagination is the right shape for a numbered-page user interface, where the caller
///     needs to jump to page 47 and see how many pages there are. It is the wrong shape for deep
///     sets and for infinite scroll: the server walks and discards every skipped row, so the cost of
///     a page grows with how far in it sits, and a row inserted or deleted between two requests
///     shifts everything after it, so a row can be shown twice or skipped. Use
///     <see cref="KeysetPagingExtensions.ToKeysetPageAsync" /> where either matters.
/// </remarks>
public static class OffsetPagingExtensions
{
    /// <summary>Reads one page, using the context's configured paging settings.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to page. Order it first, or the page is not reproducible.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="request">The page to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The page, and what the configured strategy established about the rest of the set.</returns>
    /// <example>
    ///     <code>
    ///     var page = await context.Orders
    ///         .Where(o => o.Total > 100)
    ///         .OrderBy(OrderSort, sort)
    ///         .ToPagedResultAsync(context, PageRequest.Of(pageNumber, pageSize));
    ///     </code>
    /// </example>
    public static Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> source,
        DbContext context,
        PageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PageAsync(source, context, request, cancellationToken);
    }

    /// <summary>Reads one page using <see cref="QueryOptions.Default" />.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to page. Order it first, or the page is not reproducible.</param>
    /// <param name="request">The page to read.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The page, and what the default strategy established about the rest of the set.</returns>
    /// <remarks>
    ///     For queries whose context is not to hand. Configured defaults and the advisory checks both
    ///     need the context, so neither applies here.
    /// </remarks>
    public static Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> source,
        PageRequest request,
        CancellationToken cancellationToken = default)
        => PageAsync(source, context: null, request, cancellationToken);

    /// <summary>Reads one page, using the context's configured paging settings, synchronously.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to page. Order it first, or the page is not reproducible.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="request">The page to read.</param>
    /// <returns>The page, and what the configured strategy established about the rest of the set.</returns>
    public static PagedResult<T> ToPagedResult<T>(
        this IQueryable<T> source,
        DbContext context,
        PageRequest request)
        => ToPagedResultAsync(source, context, request).GetAwaiter().GetResult();

    /// <summary>Reads one page using <see cref="QueryOptions.Default" />, synchronously.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to page. Order it first, or the page is not reproducible.</param>
    /// <param name="request">The page to read.</param>
    /// <returns>The page, and what the default strategy established about the rest of the set.</returns>
    public static PagedResult<T> ToPagedResult<T>(this IQueryable<T> source, PageRequest request)
        => ToPagedResultAsync(source, request).GetAwaiter().GetResult();

    private static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> source,
        DbContext? context,
        PageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var options = context is null
            ? QueryOptions.Default
            : QueryConfiguration.Required(context, nameof(ToPagedResultAsync));

        var page = request.Resolve(options);

        if (context is not null)
        {
            QueryAdvisor.InspectPage(context, source, page, options);
        }

        long? totalCount = null;
        if (options.CountStrategy == PageCountStrategy.TotalCount)
        {
            totalCount = await source.LongCountAsync(cancellationToken).ConfigureAwait(false);
        }

        // Under HasNextProbe one extra row is fetched and thrown away. That row is the whole answer
        // to "is there more", and it costs one row rather than the full scan a COUNT would.
        var probing = options.CountStrategy == PageCountStrategy.HasNextProbe;
        var take = probing ? page.PageSize + 1 : page.PageSize;

        var items = await source
            .Skip(page.Offset)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        bool? hasNext = null;

        if (probing)
        {
            hasNext = items.Count > page.PageSize;
            if (hasNext.Value)
            {
                items.RemoveAt(page.PageSize);
            }
        }
        else if (totalCount is { } total)
        {
            hasNext = page.Offset + items.Count < total;
        }

        return new PagedResult<T>(
            items,
            page.PageNumber,
            page.PageSize,
            totalCount,
            hasNext,
            hasPrevious: page.Offset > 0);
    }
}
