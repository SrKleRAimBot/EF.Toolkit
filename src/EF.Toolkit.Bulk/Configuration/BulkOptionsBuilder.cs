namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Fluent builder for context-wide EF.Toolkit.Bulk settings.
/// </summary>
/// <remarks>
///     Provider packages extend this type with their own knobs — for example
///     <c>UseTableLock()</c> in EF.Toolkit.Bulk.SqlServer — so provider-specific options only appear
///     once that package is installed.
/// </remarks>
public class BulkOptionsBuilder
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public BulkOptionsBuilder(BulkOptions options)
        => Options = options;

    /// <summary>The settings accumulated so far.</summary>
    public BulkOptions Options { get; protected set; }

    /// <summary>
    ///     Sets how many rows a partition must reach before bulk acceleration engages.
    /// </summary>
    /// <param name="rows">Row count; must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder Threshold(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Options = Options with { Threshold = rows };
        return this;
    }

    /// <summary>
    ///     Sets the maximum number of rows written to the server in one bulk operation.
    /// </summary>
    /// <param name="rows">Row count; must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder MaxBatchSize(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Options = Options with { MaxBatchSize = rows };
        return this;
    }

    /// <summary>
    ///     Forces upserts to use <c>MERGE</c>, or to avoid it, instead of deciding from the server
    ///     version.
    /// </summary>
    /// <param name="useMerge">
    ///     <see langword="true" /> to require it, <see langword="false" /> to avoid it.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder UseMerge(bool useMerge)
    {
        Options = Options with { UseMerge = useMerge };
        return this;
    }

    /// <summary>
    ///     Sets the row count from which a staging table is indexed on its join columns.
    /// </summary>
    /// <param name="rows">The threshold, or zero to never index.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder StagingIndexThreshold(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rows);
        Options = Options with { StagingIndexThreshold = rows };
        return this;
    }

    /// <summary>
    ///     Sets whether CHECK and foreign-key constraints are enforced on bulk-copied rows.
    /// </summary>
    /// <param name="validate">
    ///     <see langword="false" /> to skip validation. Faster, and it leaves the table's
    ///     constraints untrusted, which the query optimiser then ignores when building plans.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder ValidateConstraints(bool validate)
    {
        Options = Options with { ValidateConstraints = validate };
        return this;
    }

    /// <summary>Sets whether triggers fire for bulk-copied rows.</summary>
    /// <param name="fire"><see langword="false" /> to suppress them.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder FireTriggers(bool fire)
    {
        Options = Options with { FireTriggers = fire };
        return this;
    }

    /// <summary>Sets how an upsert works out its insert-versus-update split.</summary>
    /// <param name="counts">Exact, or the cheaper approximation.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder MergeCounts(MergeCounts counts)
    {
        Options = Options with { MergeCounts = counts };
        return this;
    }

    /// <summary>Sets what happens when a write cannot be accelerated.</summary>
    /// <param name="behavior">Fall back to stock EF, or throw.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder OnUnsupported(Unsupported behavior)
    {
        Options = Options with { OnUnsupported = behavior };
        return this;
    }

    /// <summary>Overrides the command timeout for bulk operations.</summary>
    /// <param name="timeout">The timeout to apply.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOptionsBuilder Timeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Options = Options with { Timeout = timeout };
        return this;
    }
}
