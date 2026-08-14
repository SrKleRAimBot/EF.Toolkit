namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Context-wide EF.Toolkit.Bulk settings, established by <c>UseBulkOperations()</c>.
///     Immutable; <see cref="BulkOptionsBuilder" /> produces instances via <c>with</c>.
/// </summary>
public sealed record BulkOptions
{
    /// <summary>The default value of <see cref="Threshold" />.</summary>
    public const int DefaultThreshold = 100;

    /// <summary>The default value of <see cref="MaxBatchSize" />.</summary>
    public const int DefaultMaxBatchSize = 50_000;

    /// <summary>The default value of <see cref="StagingIndexThreshold" />.</summary>
    public const int DefaultStagingIndexThreshold = 5_000;

    /// <summary>Settings used when <c>UseBulkOperations()</c> is called with no configuration.</summary>
    public static BulkOptions Default { get; } = new();

    /// <summary>
    ///     Number of rows a single partition must reach before bulk acceleration engages.
    ///     Below this, the partition runs through stock EF, whose per-row statements beat the
    ///     fixed setup cost of a bulk copy (and, where needed, a staging table) on small writes.
    /// </summary>
    public int Threshold { get; init; } = DefaultThreshold;

    /// <summary>
    ///     Maximum number of rows written to the server in one bulk operation. Larger partitions
    ///     are split. Bounds peak memory and, on SQL Server, transaction log growth.
    /// </summary>
    public int MaxBatchSize { get; init; } = DefaultMaxBatchSize;

    /// <summary>
    ///     Whether upserts may use <c>MERGE</c> where the server supports it, or
    ///     <see langword="null" /> to decide from the server version.
    /// </summary>
    /// <remarks>
    ///     PostgreSQL 17 gained <c>MERGE ... RETURNING</c> with <c>merge_action()</c>, which reports
    ///     the insert-versus-update split exactly and for free, carries a source ordinal so
    ///     generated values correlate precisely, and folds a synchronise's delete into the same
    ///     statement. Detection is automatic; this exists because a pooler or a
    ///     PostgreSQL-compatible engine can report a version whose capabilities it does not
    ///     actually have. Ignored on SQL Server, which has always used <c>MERGE</c>.
    /// </remarks>
    public bool? UseMerge { get; init; }

    /// <summary>
    ///     Row count from which a staging table gets an index on the columns the following
    ///     statement joins it by. Zero disables it entirely. Defaults to
    ///     <see cref="DefaultStagingIndexThreshold" />.
    /// </summary>
    /// <remarks>
    ///     A freshly loaded staging table is an unindexed heap with no statistics, so the planner
    ///     joins it to the target on a guess. For a large enough set that guess is what turns a
    ///     staged update into a nested loop over the whole target. Building the index costs time on
    ///     the load side, which is why it is not unconditional: below the threshold the scan is
    ///     cheaper than the sort.
    /// </remarks>
    public int StagingIndexThreshold { get; init; } = DefaultStagingIndexThreshold;

    /// <summary>
    ///     Whether CHECK and foreign-key constraints are enforced on rows written by a bulk copy.
    ///     Defaults to <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Only SQL Server can skip them: <c>SqlBulkCopy</c> disables constraint validation by
    ///         default, while PostgreSQL's <c>COPY</c> always enforces it. Skipping is faster, and
    ///         it is the wrong default for a library whose contract is that accelerating a write
    ///         does not change its result — stock <c>SaveChanges</c> enforces both.
    ///     </para>
    ///     <para>
    ///         There is a second cost that outlives the operation. Rows loaded without validation
    ///         leave the table's CHECK and foreign-key constraints marked untrusted, and the query
    ///         optimiser ignores untrusted constraints when simplifying plans — permanently, until
    ///         someone revalidates them by hand.
    ///     </para>
    /// </remarks>
    public bool ValidateConstraints { get; init; } = true;

    /// <summary>
    ///     Whether triggers fire for rows written by a bulk copy. Defaults to
    ///     <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     As with <see cref="ValidateConstraints" />, only SQL Server can skip them, and stock
    ///     <c>SaveChanges</c> does not. Turning this off is faster and makes an accelerated write
    ///     observably different from an unaccelerated one.
    /// </remarks>
    public bool FireTriggers { get; init; } = true;

    /// <summary>What to do with writes that cannot be accelerated. See <see cref="Unsupported" />.</summary>
    public Unsupported OnUnsupported { get; init; } = Unsupported.FallBack;

    /// <summary>
    ///     How an upsert works out its insert-versus-update split. See <see cref="MergeCounts" />.
    /// </summary>
    /// <remarks>
    ///     Defaults to <see cref="MergeCounts.Exact" />. SQL Server reports the split exactly at no
    ///     cost, so a default of anything else would make the same property mean different things
    ///     on different databases — and the cost of being right on PostgreSQL is one round trip on
    ///     an operation that is not usually the hot path.
    /// </remarks>
    public MergeCounts MergeCounts { get; init; } = MergeCounts.Exact;

    /// <summary>
    ///     Overrides the command timeout for bulk operations. When <see langword="null" />, the
    ///     context's configured command timeout applies.
    /// </summary>
    public TimeSpan? Timeout { get; init; }
}
