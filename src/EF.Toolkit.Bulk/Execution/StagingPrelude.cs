namespace EFToolkit.Bulk.Execution;

/// <summary>
///     The statements that prepare a freshly loaded staging table for the statement that joins it.
/// </summary>
/// <remarks>
///     <para>
///         A staging table arrives as an unindexed heap the planner knows nothing about. On
///         PostgreSQL that is worse than it sounds: autovacuum never touches temporary tables, so
///         there are no statistics at all and the planner works from a hard-coded row estimate.
///         Joined against a large target, that is exactly the input that produces a nested loop
///         over the whole table.
///     </para>
///     <para>
///         Both fixes are cheap, and both are prepended to the statement that needs them rather
///         than sent on their own — a separate round trip for each would cost more than they save.
///     </para>
/// </remarks>
internal static class StagingPrelude
{
    /// <summary>Whether a staging table of this size should be indexed before it is joined.</summary>
    /// <param name="rowCount">Rows written to the staging table.</param>
    /// <param name="joinColumns">Number of columns the following statement joins by.</param>
    /// <param name="threshold">
    ///     <see cref="Configuration.BulkOptions.StagingIndexThreshold" />; zero disables indexing.
    /// </param>
    /// <remarks>
    ///     Building the index is a sort over the staged rows, which is only worth paying for when
    ///     the join would otherwise scan them repeatedly. Below the threshold the scan wins, and
    ///     with nothing to join on there is nothing to index.
    /// </remarks>
    public static bool ShouldIndex(int rowCount, int joinColumns, int threshold)
        => threshold > 0 && joinColumns > 0 && rowCount >= threshold;
}
