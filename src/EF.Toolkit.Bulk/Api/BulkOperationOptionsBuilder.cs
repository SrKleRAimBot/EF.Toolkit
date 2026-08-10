using System.Linq.Expressions;
using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Fluent, strongly-typed settings for a single bulk call.
/// </summary>
/// <typeparam name="TEntity">The entity type being written.</typeparam>
public class BulkOperationOptionsBuilder<TEntity>
    where TEntity : class
{
    /// <summary>Initializes a new builder seeded with <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public BulkOperationOptionsBuilder(BulkOperationOptions options)
        => Options = options;

    /// <summary>The settings accumulated so far.</summary>
    public BulkOperationOptions Options { get; protected set; }

    /// <summary>
    ///     Attaches the entities to the change tracker as <c>Unchanged</c> when the call completes.
    /// </summary>
    /// <remarks>
    ///     Only needed when you intend to keep working with the entities through the same context.
    ///     Store-generated keys are written onto your objects whether or not you call this.
    /// </remarks>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> Track()
    {
        Options = Options with { Track = true };
        return this;
    }

    /// <summary>
    ///     Chooses the columns a merge matches on. Defaults to the primary key.
    /// </summary>
    /// <param name="match">
    ///     A property, or an anonymous type of properties: <c>c =&gt; c.Email</c> or
    ///     <c>c =&gt; new { c.TenantId, c.Email }</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     The match columns need a unique index. PostgreSQL's <c>ON CONFLICT</c> requires one to
    ///     define what a conflict is, and without one on SQL Server a <c>MERGE</c> would happily
    ///     match several rows at once.
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> MatchOn(
        Expression<Func<TEntity, object?>> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        Options = Options with { Match = match };
        return this;
    }

    /// <summary>
    ///     Follows navigations and writes everything reachable, principals before dependents.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Foreign keys are filled in from the navigations as each principal is written, which is
    ///     the job change tracking would normally do — so assigning <c>order.Customer</c> is enough
    ///     and <c>order.CustomerId</c> takes care of itself.
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> IncludeGraph()
    {
        Options = Options with { IncludeGraph = true };
        return this;
    }

    /// <summary>
    ///     Sets how this upsert works out its insert-versus-update split.
    /// </summary>
    /// <param name="counts">Exact, or the cheaper approximation.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Affects only the numbers in <see cref="BulkResult" /> — both settings write identical
    ///     data — and only on PostgreSQL, since SQL Server reports the split exactly for free.
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> MergeCounts(MergeCounts counts)
    {
        Options = Options with { MergeCounts = counts };
        return this;
    }

    /// <summary>
    ///     Runs the operation without a savepoint when it is inside a transaction the caller
    ///     opened.
    /// </summary>
    /// <remarks>
    ///     Saves a round trip. In exchange, a failure aborts the caller's whole transaction on
    ///     engines that work that way, rather than only this operation.
    /// </remarks>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> WithoutSavepoint()
    {
        Options = Options with { Savepoint = false };
        return this;
    }

    /// <summary>Sets the maximum number of rows sent to the server in one operation.</summary>
    /// <param name="rows">Row count; must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> BatchSize(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Options = Options with { BatchSize = rows };
        return this;
    }

    /// <summary>Sets the command timeout for this operation.</summary>
    /// <param name="timeout">The timeout to apply.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> Timeout(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        Options = Options with { Timeout = timeout };
        return this;
    }

    /// <summary>Reports progress as rows are written.</summary>
    /// <param name="onProgress">Invoked after each batch.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> OnProgress(Action<BulkProgress> onProgress)
    {
        ArgumentNullException.ThrowIfNull(onProgress);
        Options = Options with { Progress = onProgress };
        return this;
    }
}
