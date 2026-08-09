using System.Linq.Expressions;
using EFToolkit.Bulk;
using EFToolkit.Bulk.Api;
using EFToolkit.Bulk.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using EFToolkit.Bulk.Execution;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Explicit bulk operations on a <see cref="DbContext" />.
/// </summary>
/// <remarks>
///     <para>
///         These bypass EF's update pipeline entirely — no change detection, no modification
///         commands, no dependency graph — which is what separates them from transparent
///         <c>SaveChanges()</c> acceleration. Measurement put that pipeline at roughly 70% of a
///         transparent save's cost, so this is where large speedups come from.
///     </para>
///     <para>
///         The trade is that you take responsibility for ordering: insert principals before their
///         dependents, exactly as you would writing the SQL yourself.
///     </para>
/// </remarks>
public static class DbContextBulkExtensions
{
    /// <summary>Inserts <paramref name="entities" /> using the database's native bulk loader.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the operation did.</returns>
    /// <example>
    ///     <code>
    ///     await context.BulkInsertAsync(orders);
    ///     orders[0].Id;                  // populated
    ///     context.Entry(orders[0]).State // Detached
    ///
    ///     await context.BulkInsertAsync(orders, o => o.Track().BatchSize(20_000));
    ///     </code>
    /// </example>
    public static Task<BulkResult> BulkInsertAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var options = Build(configure);
        var materialized = entities as IReadOnlyList<TEntity> ?? [.. entities];

        return options.IncludeGraph
            ? BulkOperations.InsertGraphAsync(context, materialized, options, cancellationToken)
            : BulkOperations.ExecuteAsync(
                context, materialized, EntityState.Added, BulkOperationKind.Insert,
                matchProperties: null, options, cancellationToken);
    }

    /// <summary>
    ///     Inserts <paramref name="entities" />, streaming from an asynchronous source.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the operation did.</returns>
    public static async Task<BulkResult> BulkInsertAsync<TEntity>(
        this DbContext context,
        IAsyncEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var buffered = new List<TEntity>();
        await foreach (var entity in entities.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffered.Add(entity);
        }

        return await BulkOperations
            .ExecuteAsync(
                context, buffered, EntityState.Added, BulkOperationKind.Insert,
                matchProperties: null, Build(configure), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="entities" />, synchronously.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to insert.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <returns>What the operation did.</returns>
    public static BulkResult BulkInsert<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BulkInsertAsync(context, entities, configure).GetAwaiter().GetResult();

    /// <summary>Updates <paramref name="entities" /> by primary key, as one set-based statement.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to update. Every non-key mapped column is written.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the operation did.</returns>
    /// <remarks>
    ///     Unlike <c>SaveChanges()</c>, which writes only the properties it detected as changed,
    ///     this writes every non-key column — there is no change tracking involved to tell it
    ///     otherwise. A row that has gone missing raises
    ///     <see cref="DbUpdateConcurrencyException" />, matching stock EF.
    /// </remarks>
    public static Task<BulkResult> BulkUpdateAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        return BulkOperations.ExecuteAsync(
            context,
            entities as IReadOnlyList<TEntity> ?? [.. entities],
            EntityState.Modified,
            BulkOperationKind.Update,
            matchProperties: null,
            Build(configure),
            cancellationToken);
    }

    /// <summary>Updates <paramref name="entities" />, synchronously.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to update.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <returns>What the operation did.</returns>
    public static BulkResult BulkUpdate<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BulkUpdateAsync(context, entities, configure).GetAwaiter().GetResult();

    /// <summary>Deletes <paramref name="entities" /> by primary key, as one set-based statement.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to delete. Only their keys are read.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>What the operation did.</returns>
    /// <remarks>
    ///     The entities end up <c>Detached</c> whatever the tracking setting says: their rows no
    ///     longer exist, so leaving them <c>Unchanged</c> would assert otherwise.
    /// </remarks>
    public static Task<BulkResult> BulkDeleteAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        return BulkOperations.ExecuteAsync(
            context,
            entities as IReadOnlyList<TEntity> ?? [.. entities],
            EntityState.Deleted,
            BulkOperationKind.Delete,
            matchProperties: null,
            Build(configure),
            cancellationToken);
    }

    /// <summary>Deletes <paramref name="entities" />, synchronously.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to delete.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <returns>What the operation did.</returns>
    public static BulkResult BulkDelete<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BulkDeleteAsync(context, entities, configure).GetAwaiter().GetResult();

    /// <summary>
    ///     Inserts rows that do not exist and updates those that do, in one statement.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to upsert.</param>
    /// <param name="configure">
    ///     Optional per-call settings. Use <c>MatchOn</c> to match on something other than the
    ///     primary key.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were inserted and how many updated.</returns>
    /// <remarks>
    ///     <para>
    ///         The database decides insert-versus-update per row — <c>INSERT ... ON CONFLICT</c> on
    ///         PostgreSQL, <c>MERGE</c> on SQL Server — so there is no read-then-write race.
    ///     </para>
    ///     <para>
    ///         The match columns must have a unique index. Store-generated keys are populated on the
    ///         entities that turned out to be inserts.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     var r = await context.BulkMergeAsync(customers, o => o.MatchOn(c => c.Email));
    ///     Console.WriteLine($"{r.Inserted} new, {r.Updated} existing");
    ///     </code>
    /// </example>
    public static Task<BulkResult> BulkMergeAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var options = Build(configure);

        return BulkOperations.ExecuteAsync(
            context,
            entities as IReadOnlyList<TEntity> ?? [.. entities],
            EntityState.Added,
            BulkOperationKind.Merge,
            MatchProperties<TEntity>(context, options),
            options,
            cancellationToken);
    }

    /// <summary>Upserts <paramref name="entities" />, synchronously.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The entities to upsert.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <returns>How many rows were inserted and how many updated.</returns>
    public static BulkResult BulkMerge<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BulkMergeAsync(context, entities, configure).GetAwaiter().GetResult();

    /// <summary>
    ///     Makes the table match <paramref name="entities" /> exactly: inserts, updates, and deletes
    ///     everything else.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The rows the table should end up containing.</param>
    /// <param name="configure">Optional per-call settings, including <c>MatchOn</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were inserted, updated and deleted.</returns>
    /// <remarks>
    ///     <para>
    ///         <strong>The delete covers the entire table.</strong> Any row whose match value is not
    ///         in <paramref name="entities" /> is removed — that is what makes this a synchronise
    ///         rather than a merge, and it is easy to invoke with a partial list by accident. Use
    ///         <c>BulkMergeAsync</c> if you only mean to upsert.
    ///     </para>
    ///     <para>
    ///         Runs as a single transaction, so the table is never observed half-synchronised.
    ///     </para>
    /// </remarks>
    public static Task<BulkResult> BulkSynchronizeAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var options = Build(configure);
        var materialized = entities as IReadOnlyList<TEntity> ?? [.. entities];

        if (materialized.Count == 0)
        {
            // Synchronising to nothing would empty the table. That is a defensible reading, but it
            // is far more often a bug in the caller's query than an intent to truncate.
            throw new BulkNotSupportedException(
                $"BulkSynchronizeAsync was given no {typeof(TEntity).Name} rows, which would "
                + "delete every row in the table. Call BulkDeleteAsync explicitly if that is the "
                + "intent.");
        }

        return BulkOperations.ExecuteAsync(
            context,
            materialized,
            EntityState.Added,
            BulkOperationKind.Synchronize,
            MatchProperties<TEntity>(context, options),
            options,
            cancellationToken);
    }

    /// <summary>Synchronises the table to <paramref name="entities" />, synchronously.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="entities">The rows the table should end up containing.</param>
    /// <param name="configure">Optional per-call settings.</param>
    /// <returns>How many rows were inserted, updated and deleted.</returns>
    public static BulkResult BulkSynchronize<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        Action<BulkOperationOptionsBuilder<TEntity>>? configure = null)
        where TEntity : class
        => BulkSynchronizeAsync(context, entities, configure).GetAwaiter().GetResult();

    /// <summary>
    ///     Saves the change tracker's pending changes through EF.Toolkit.Bulk, without needing
    ///     <c>UseBulkOperations()</c> to have made it the default.
    /// </summary>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="acceptAllChangesOnSuccess">
    ///     Whether to accept all changes on success, as <c>SaveChangesAsync</c> does.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of state entries written.</returns>
    /// <remarks>
    ///     <para>
    ///         This is ordinary <c>SaveChangesAsync</c>. Once <c>UseBulkOperations()</c> is applied,
    ///         every save already goes through EF.Toolkit.Bulk's batch factory, so there is nothing extra to
    ///         do — the method exists because the name is what people look for, and finding it and
    ///         learning that it is a synonym is better than not finding it and assuming saves are
    ///         not being accelerated.
    ///     </para>
    ///     <para>
    ///         Being a genuine save, it keeps every guarantee <c>SaveChanges</c> makes: dependency
    ///         ordering, change tracking, interceptors. It is also therefore bounded by EF's own
    ///         pipeline — see the explicit methods above when that ceiling matters.
    ///     </para>
    /// </remarks>
    public static Task<int> BulkSaveChangesAsync(
        this DbContext context,
        bool acceptAllChangesOnSuccess = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Resolved purely to fail loudly when EF.Toolkit.Bulk is not configured; without the check this
        // would silently be a plain, unaccelerated save.
        _ = context.GetService<BulkOptions>()
            ?? throw new InvalidOperationException(
                "EF.Toolkit.Bulk is not configured for this context. Add UseBulkOperations() alongside "
                + "your provider, for example: "
                + "options.UseNpgsql(connectionString).UseBulkOperations().");

        return context.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Saves the change tracker's pending changes through EF.Toolkit.Bulk, synchronously.</summary>
    /// <param name="context">The context. Must be configured with <c>UseBulkOperations()</c>.</param>
    /// <param name="acceptAllChangesOnSuccess">Whether to accept all changes on success.</param>
    /// <returns>The number of state entries written.</returns>
    public static int BulkSaveChanges(this DbContext context, bool acceptAllChangesOnSuccess = true)
        => BulkSaveChangesAsync(context, acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    private static IReadOnlyList<IProperty> MatchProperties<TEntity>(
        DbContext context,
        BulkOperationOptions options)
        where TEntity : class
    {
        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new BulkNotSupportedException(
                $"'{typeof(TEntity).Name}' is not part of the model for "
                + $"'{context.GetType().Name}'.");

        return options.Match is Expression<Func<TEntity, object?>> selector
            ? MatchPropertySelector.Resolve(entityType, selector)
            : entityType.FindPrimaryKey()?.Properties
                ?? throw new BulkNotSupportedException(
                    $"'{typeof(TEntity).Name}' has no primary key, so matching needs an explicit "
                    + "MatchOn.");
    }

    private static BulkOperationOptions Build<TEntity>(
        Action<BulkOperationOptionsBuilder<TEntity>>? configure)
        where TEntity : class
    {
        if (configure is null)
        {
            return BulkOperationOptions.Default;
        }

        var builder = new BulkOperationOptionsBuilder<TEntity>(BulkOperationOptions.Default);
        configure(builder);
        return builder.Options;
    }
}
