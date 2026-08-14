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
    ///     Chooses the columns the operation matches rows on. Defaults to the primary key.
    /// </summary>
    /// <param name="match">
    ///     A property, or an anonymous type of properties: <c>c =&gt; c.Email</c> or
    ///     <c>c =&gt; new { c.TenantId, c.Email }</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         On a merge or a synchronise the match columns need a unique index. PostgreSQL's
    ///         <c>ON CONFLICT</c> requires one to define what a conflict is, and without one on SQL
    ///         Server a <c>MERGE</c> would happily match several rows at once.
    ///     </para>
    ///     <para>
    ///         An update or a delete needs no such index, because a set-based <c>UPDATE</c> or
    ///         <c>DELETE</c> has no problem with one source row reaching several target rows — but
    ///         that also means it will. <c>BulkResult</c> then reports how many of the entities you
    ///         passed found a row, not how many rows changed, and a row that has gone missing still
    ///         raises <c>DbUpdateConcurrencyException</c>.
    ///     </para>
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> MatchOn(
        Expression<Func<TEntity, object?>> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        Options = Options with { Match = match };
        return this;
    }

    /// <summary>Writes only the columns named here.</summary>
    /// <param name="columns">
    ///     A property, or an anonymous type of properties: <c>o =&gt; o.Status</c> or
    ///     <c>o =&gt; new { o.Status, o.ShippedAt }</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Most useful on <c>BulkUpdateAsync</c>, which otherwise writes every non-key column
    ///     because it has no change tracking to tell it which ones you touched. Store-generated
    ///     columns are unaffected — naming one here does not make it writable — and the match
    ///     columns cannot be dropped, since they are what locate the row.
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> Include(
        Expression<Func<TEntity, object?>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Options = Options with { Include = columns };
        return this;
    }

    /// <summary>Writes every column except those named here.</summary>
    /// <param name="columns">
    ///     A property, or an anonymous type of properties: <c>o =&gt; o.CreatedAt</c> or
    ///     <c>o =&gt; new { o.CreatedAt, o.CreatedBy }</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>Cannot be combined with <see cref="Include" />, which already states the whole set.</remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> Exclude(
        Expression<Func<TEntity, object?>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Options = Options with { Exclude = columns };
        return this;
    }

    /// <summary>
    ///     Writes the named columns when a merge inserts a row, and leaves them alone when it
    ///     updates one.
    /// </summary>
    /// <param name="columns">
    ///     A property, or an anonymous type of properties: <c>c =&gt; c.CreatedAt</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     For the columns that record how a row came to exist — created-at, created-by, an
    ///     original import id. Only a merge or a synchronise has both arms, so this is rejected on
    ///     any other operation rather than quietly ignored. Marking every writable column
    ///     insert-only leaves the update arm empty, which is a legitimate "insert if missing, leave
    ///     alone otherwise".
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> InsertOnly(
        Expression<Func<TEntity, object?>> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        Options = Options with { InsertOnly = columns };
        return this;
    }

    /// <summary>
    ///     Confines a synchronise's delete arm to the rows matching <paramref name="scope" />.
    /// </summary>
    /// <param name="scope">
    ///     A predicate over mapped properties, for example <c>r =&gt; r.TenantId == tenantId</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         This is what makes a synchronise usable against a table you share: the source is your
    ///         slice, so the delete has to be your slice too. With a scope,
    ///         <c>AllowFullTableDelete()</c> is not needed — the delete is no longer full-table.
    ///     </para>
    ///     <para>
    ///         The translator is deliberately narrow: <c>&amp;&amp;</c>-ed comparisons between a
    ///         mapped property and a value. Anything it cannot take is refused with a message
    ///         naming it, rather than silently widening what gets deleted. Values are bound as
    ///         parameters.
    ///     </para>
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> WithinScope(
        Expression<Func<TEntity, bool>> scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Options = Options with { Scope = scope, ScopeSql = null };
        return this;
    }

    /// <summary>
    ///     Confines a synchronise's delete arm using SQL, for a predicate the expression translator
    ///     does not cover.
    /// </summary>
    /// <param name="scope">
    ///     The predicate, with its values interpolated:
    ///     <c>$"t.tenant_id = {tenantId} AND t.archived_at IS NULL"</c>.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     The target table is aliased <c>t</c>, and column names are the database's rather than the
    ///     model's. Every interpolation hole becomes a bound parameter, so a value coming from
    ///     outside the application is safe here — and a column or table name cannot be interpolated
    ///     at all, since it would arrive as a parameter where the server expects an identifier. The
    ///     rest of the string is yours and is used verbatim.
    /// </remarks>
    public virtual BulkOperationOptionsBuilder<TEntity> WithinScope(FormattableString scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Options = Options with { ScopeSql = scope, Scope = null };
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
    ///     Permits a synchronise to delete every row of the table its source does not contain.
    /// </summary>
    /// <remarks>
    ///     <c>BulkSynchronizeAsync</c> refuses to run without this or <see cref="WithinScope(Expression{Func{TEntity, bool}})" />.
    ///     Its delete arm covers the whole table — that is what separates it from a merge — so
    ///     calling it with a partial list removes everything the list omitted. That is easy to do by
    ///     accident, impossible to undo, and costs one method call to rule out. Where the list is
    ///     partial by design, a scope is the better answer: it fences the delete instead of
    ///     confirming it.
    /// </remarks>
    /// <returns>The same builder, for chaining.</returns>
    public virtual BulkOperationOptionsBuilder<TEntity> AllowFullTableDelete()
    {
        Options = Options with { AllowFullTableDelete = true };
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
