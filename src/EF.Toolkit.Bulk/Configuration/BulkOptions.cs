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
