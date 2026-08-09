namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     How <c>BulkMergeAsync</c> and <c>BulkSynchronizeAsync</c> work out how many rows they
///     inserted versus updated.
/// </summary>
/// <remarks>
///     <para>
///         This only ever affects the numbers reported in <see cref="Api.BulkResult" />. Both
///         settings write exactly the same data.
///     </para>
///     <para>
///         It also only matters on PostgreSQL. SQL Server's <c>MERGE</c> reports <c>$action</c> per
///         row, which says outright what happened to it, so counts are exact there at no cost and
///         this setting is ignored.
///     </para>
/// </remarks>
public enum MergeCounts
{
    /// <summary>
    ///     Determine the split by counting the rows that already exist, immediately before the
    ///     merge and inside the same transaction.
    /// </summary>
    /// <remarks>
    ///     Costs one extra round trip on PostgreSQL — an indexed existence check over the staged
    ///     match values. Accurate unless another transaction inserts a matching row in the gap
    ///     between the count and the merge, which sharing a transaction makes a narrow window.
    /// </remarks>
    Exact,

    /// <summary>
    ///     Determine the split from each returned row's <c>xmax</c>, which PostgreSQL leaves at
    ///     zero on a freshly inserted tuple.
    /// </summary>
    /// <remarks>
    ///     Free — the value comes back with rows the statement already returns — but it is a
    ///     widely-used convention rather than a documented guarantee, and it can misreport under
    ///     concurrent access. Choose this when the counts are informational and throughput is what
    ///     matters.
    /// </remarks>
    Approximate
}
