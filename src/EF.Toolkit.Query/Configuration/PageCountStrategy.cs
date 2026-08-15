namespace EFToolkit.Query.Configuration;

/// <summary>How an offset-paginated query works out what lies beyond the page it returned.</summary>
public enum PageCountStrategy
{
    /// <summary>
    ///     Run a <c>COUNT</c> alongside the page. Fills in
    ///     <see cref="Paging.PagedResult{T}.TotalCount" /> and
    ///     <see cref="Paging.PagedResult{T}.TotalPages" /> at the cost of a second round trip that
    ///     scans every matching row.
    /// </summary>
    TotalCount = 0,

    /// <summary>
    ///     Fetch one row more than the page size and discard it. Answers
    ///     <see cref="Paging.PagedResult{T}.HasNext" /> in a single round trip, and leaves
    ///     <see cref="Paging.PagedResult{T}.TotalCount" /> unset.
    /// </summary>
    /// <remarks>
    ///     The right default for an infinite-scroll list, where nothing displays a total but the
    ///     client still needs to know whether to keep going.
    /// </remarks>
    HasNextProbe = 1,

    /// <summary>
    ///     Fetch the page and nothing else. Both <see cref="Paging.PagedResult{T}.TotalCount" /> and
    ///     <see cref="Paging.PagedResult{T}.HasNext" /> are left unset.
    /// </summary>
    None = 2,
}
