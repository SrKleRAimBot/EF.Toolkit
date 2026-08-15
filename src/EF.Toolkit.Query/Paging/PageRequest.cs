using EFToolkit.Query.Configuration;

namespace EFToolkit.Query.Paging;

/// <summary>
///     A page as the caller asked for it, before the context's configured defaults and ceiling have
///     been applied.
/// </summary>
/// <remarks>
///     Deliberately holds what arrived rather than what will happen. The page size is nullable
///     because "the caller did not say" and "the caller asked for the number that happens to be the
///     default" are different facts, and only the first should follow a later change to the
///     configured default.
/// </remarks>
public sealed record PageRequest
{
    private PageRequest(int pageNumber, int? pageSize)
    {
        PageNumber = pageNumber;
        PageSize = pageSize;
    }

    /// <summary>The requested page number, in whichever base the context is configured for.</summary>
    public int PageNumber { get; }

    /// <summary>
    ///     The requested page size, or <see langword="null" /> to use the configured default.
    /// </summary>
    public int? PageSize { get; }

    /// <summary>Asks for a page.</summary>
    /// <param name="pageNumber">
    ///     The page number. Must not be negative, and must be at least the first page number for the
    ///     context's configured <see cref="PageNumbering" /> — which is only known once the request is
    ///     resolved against a context.
    /// </param>
    /// <param name="pageSize">
    ///     Rows per page, or <see langword="null" /> for the configured default. Must be at least 1.
    ///     Sizes above the configured ceiling are clamped down to it rather than refused.
    /// </param>
    /// <returns>The request.</returns>
    public static PageRequest Of(int pageNumber, int? pageSize = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageNumber);

        if (pageSize is { } size)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        }

        return new PageRequest(pageNumber, pageSize);
    }

    /// <summary>Applies <paramref name="options" /> to work out the page actually being read.</summary>
    /// <exception cref="QueryNotSupportedException">
    ///     The page number is below the first page under the configured numbering, or the page sits so
    ///     far into the set that its offset does not fit in the <c>int</c> <c>Skip</c> takes.
    /// </exception>
    internal ResolvedPage Resolve(QueryOptions options)
    {
        var first = options.FirstPageNumber;

        if (PageNumber < first)
        {
            throw new QueryNotSupportedException(
                $"Page {PageNumber} is before the first page. This context is configured for "
                + $"{options.Numbering} page numbering, so the first page is page {first}. Either ask "
                + $"for page {first} or configure PageNumbering("
                + $"{(first == 1 ? "PageNumbering.ZeroBased" : "PageNumbering.OneBased")}).");
        }

        var requested = PageSize ?? options.DefaultPageSize;
        var size = Math.Min(requested, options.MaxPageSize);

        // Computed in long: a caller-supplied page number near int.MaxValue multiplied by any page
        // size overflows, and an overflowed offset silently reads the wrong page rather than failing.
        var offset = (long)(PageNumber - first) * size;

        if (offset > int.MaxValue)
        {
            throw new QueryNotSupportedException(
                $"Page {PageNumber} at {size} rows per page starts at row {offset}, which is beyond the "
                + "largest offset a query can express. Use keyset pagination "
                + "(ToKeysetPageAsync) for sets this deep — it does not count from the start.");
        }

        return new ResolvedPage(PageNumber, size, (int)offset, WasClamped: requested > size);
    }
}

/// <summary>A page request with the context's configuration applied.</summary>
/// <param name="PageNumber">The page being read.</param>
/// <param name="PageSize">Rows per page after defaulting and clamping.</param>
/// <param name="Offset">Rows to skip before the page.</param>
/// <param name="WasClamped">Whether the caller asked for more rows than the configured ceiling.</param>
internal readonly record struct ResolvedPage(int PageNumber, int PageSize, int Offset, bool WasClamped);
