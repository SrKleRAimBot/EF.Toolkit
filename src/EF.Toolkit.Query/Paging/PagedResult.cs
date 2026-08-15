using EFToolkit.Query.Configuration;

namespace EFToolkit.Query.Paging;

/// <summary>One page of an offset-paginated query, with what is known about the rest of the set.</summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     <see cref="TotalCount" /> and <see cref="HasNext" /> are nullable because what is known depends
///     on the configured <see cref="PageCountStrategy" />. A result that reported <c>0</c> or
///     <see langword="false" /> when it had not looked would be indistinguishable from one that had.
/// </remarks>
public sealed record PagedResult<T>
{
    internal PagedResult(
        IReadOnlyList<T> items,
        int pageNumber,
        int pageSize,
        long? totalCount,
        bool? hasNext,
        bool hasPrevious)
    {
        Items = items;
        PageNumber = pageNumber;
        PageSize = pageSize;
        TotalCount = totalCount;
        HasNext = hasNext;
        HasPrevious = hasPrevious;
    }

    /// <summary>The rows on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The page number, in the context's configured base.</summary>
    public int PageNumber { get; }

    /// <summary>Rows per page, after the configured default and ceiling were applied.</summary>
    public int PageSize { get; }

    /// <summary>
    ///     Rows matching the query in total, or <see langword="null" /> when the configured strategy
    ///     did not count them.
    /// </summary>
    public long? TotalCount { get; }

    /// <summary>
    ///     Whether another page follows, or <see langword="null" /> under
    ///     <see cref="PageCountStrategy.None" />, which does not look.
    /// </summary>
    public bool? HasNext { get; }

    /// <summary>Whether a page precedes this one.</summary>
    public bool HasPrevious { get; }

    /// <summary>
    ///     Pages in the set, or <see langword="null" /> when <see cref="TotalCount" /> is unknown.
    /// </summary>
    public int? TotalPages => TotalCount is { } total
        ? (int)Math.Min((total + PageSize - 1) / PageSize, int.MaxValue)
        : null;

    /// <summary>Whether this page has no rows.</summary>
    public bool IsEmpty => Items.Count == 0;
}
