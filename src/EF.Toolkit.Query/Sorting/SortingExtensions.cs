using EFToolkit.Query.Sorting;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Orders a query through a <see cref="SortSpecification{T}" />.</summary>
public static class SortingExtensions
{
    /// <summary>Orders <paramref name="source" /> by what the caller asked for.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to order.</param>
    /// <param name="specification">The orderings this query offers.</param>
    /// <param name="request">
    ///     What the caller asked for. <see langword="null" /> applies the specification's default.
    /// </param>
    /// <returns>The ordered query, with the specification's tiebreaker appended.</returns>
    /// <example>
    ///     <code>
    ///     var ordered = context.Orders
    ///         .Where(o => o.Total > 100)
    ///         .OrderBy(OrderSort, SortRequest.Parse(sortQueryString));
    ///     </code>
    /// </example>
    public static IOrderedQueryable<T> OrderBy<T>(
        this IQueryable<T> source,
        SortSpecification<T> specification,
        SortRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return specification.Apply(source, request);
    }

    /// <summary>Orders <paramref name="source" /> by an unparsed sort expression.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to order.</param>
    /// <param name="specification">The orderings this query offers.</param>
    /// <param name="sort">
    ///     A comma-separated ordering such as <c>"total:desc,placed"</c>. See
    ///     <see cref="SortRequest.Parse" /> for the format. <see langword="null" />, empty or
    ///     whitespace applies the specification's default.
    /// </param>
    /// <returns>The ordered query, with the specification's tiebreaker appended.</returns>
    public static IOrderedQueryable<T> OrderBy<T>(
        this IQueryable<T> source,
        SortSpecification<T> specification,
        string? sort)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return specification.Apply(source, SortRequest.Parse(sort));
    }
}
