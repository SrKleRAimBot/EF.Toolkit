namespace EFToolkit.Query.Paging;

/// <summary>One page of a keyset-paginated query, with the cursors that reach its neighbours.</summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
///     There is no total count and no page number. Keyset pagination never counts from the start of
///     the set — that is the whole point of it — so neither is available without a second query the
///     caller can run for itself.
/// </remarks>
public sealed record KeysetPage<T>
{
    internal KeysetPage(
        IReadOnlyList<T> items,
        int pageSize,
        KeysetCursor? next,
        KeysetCursor? previous,
        bool hasNext,
        bool hasPrevious)
    {
        Items = items;
        PageSize = pageSize;
        Next = next;
        Previous = previous;
        HasNext = hasNext;
        HasPrevious = hasPrevious;
    }

    /// <summary>The rows on this page, in the definition's order.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Rows per page, after the configured default and ceiling were applied.</summary>
    public int PageSize { get; }

    /// <summary>
    ///     Reads the following page, or <see langword="null" /> when there is none — or when this page
    ///     is empty, in which case there is no row to anchor a cursor to and the caller should reuse
    ///     the cursor it already has.
    /// </summary>
    public KeysetCursor? Next { get; }

    /// <summary>
    ///     Reads the preceding page, or <see langword="null" /> when there is none or this page is
    ///     empty.
    /// </summary>
    public KeysetCursor? Previous { get; }

    /// <summary>Whether another page follows.</summary>
    public bool HasNext { get; }

    /// <summary>Whether a page precedes this one.</summary>
    public bool HasPrevious { get; }

    /// <summary>Whether this page has no rows.</summary>
    public bool IsEmpty => Items.Count == 0;
}
