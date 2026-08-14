using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Drives the explicit bulk API: splits the input, writes it through the provider's executor,
///     and leaves the change tracker in the requested state.
/// </summary>
internal static class BulkOperations
{
    public static async Task<BulkResult> ExecuteAsync<TEntity>(
        DbContext context,
        IReadOnlyList<TEntity> entities,
        EntityState state,
        BulkOperationKind kind,
        IReadOnlyList<IProperty>? matchProperties,
        BulkOperationOptions options,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (entities.Count == 0)
        {
            return Result(kind, 0);
        }

        var bulkOptions = Resolve<BulkOptions>(context, "UseBulkOperations()");
        var executor = Resolve<IBulkOperationExecutor>(context, "UseBulkOperations()");
        var connection = context.GetService<IRelationalConnection>();

        var entityType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new BulkNotSupportedException(
                $"'{typeof(TEntity).Name}' is not part of the model for "
                + $"'{context.GetType().Name}'.");

        var plan = matchProperties is null
            ? BulkEntityPlan.For(entityType, state)
            : BulkEntityPlan.ForMerge(entityType, matchProperties);
        // A synchronise removes every row its source does not contain, so it is the one operation
        // that cannot be split: the second batch's delete arm would remove exactly what the first
        // batch had just written. It runs as a single unit, which does mean holding the whole
        // source in one staging table.
        var batchSize = kind == BulkOperationKind.Synchronize
            ? int.MaxValue
            : options.BatchSize ?? bulkOptions.MaxBatchSize;

        var mergeCounts = options.MergeCounts ?? bulkOptions.MergeCounts;

        // Take the whole operation as one unit, matching SaveChanges: a partially-applied bulk
        // insert is far harder to reason about than a slow one.
        var outcome = await BulkTransaction
            .RunAsync(
                context,
                options.Savepoint,
                async ct => await WriteBatchesAsync(
                        context, entities, state, kind, plan, executor, connection, batchSize,
                        mergeCounts, options, ct)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

        // Only once the write has succeeded. When this owns the transaction that means committed,
        // so a failure leaves the tracker untouched. When the caller owns it -- their own
        // transaction, or an ambient scope -- the write is durable only when they commit, and
        // reconciling here matches what SaveChanges does in the same position: the tracker follows
        // the write, and a caller who rolls back is expected to discard the context with it.
        Reconcile(context, entities, state, options.Track);

        return outcome;
    }

    /// <summary>
    ///     Writes the entities in batches, falling back to stock EF Core if the provider declines.
    /// </summary>
    /// <remarks>
    ///     A fallback returns its result rather than returning out of the caller. Returning early
    ///     used to skip the commit and let the surrounding <c>finally</c> dispose the transaction,
    ///     so the fallback's own <c>SaveChanges</c> was rolled back while the call still reported
    ///     success — the writes were silently discarded.
    /// </remarks>
    private static async Task<BulkResult> WriteBatchesAsync<TEntity>(
        DbContext context,
        IReadOnlyList<TEntity> entities,
        EntityState state,
        BulkOperationKind kind,
        BulkEntityPlan plan,
        IBulkOperationExecutor executor,
        IRelationalConnection connection,
        int batchSize,
        MergeCounts mergeCounts,
        BulkOperationOptions options,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var written = 0;
        var merged = new BulkResult();

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            for (var offset = 0; offset < entities.Count; offset += batchSize)
            {
                var slice = Slice(entities, offset, Math.Min(batchSize, entities.Count - offset));
                var rows = new EntityRowSet(slice, plan, state, kind, mergeCounts, options.Timeout);

                if (!executor.CanExecute(rows, out var reason))
                {
                    return await FallBackAsync(
                            context, entities, state, kind, reason, cancellationToken)
                        .ConfigureAwait(false);
                }

                var result = await executor
                    .ExecuteAsync(rows, connection, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Handled)
                {
                    return await FallBackAsync(
                            context, entities, state, kind, result.DeclinedReason,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                written += result.RowsAffected;
                merged = merged with
                {
                    Inserted = merged.Inserted + result.Inserted,
                    Updated = merged.Updated + result.Updated,
                    Deleted = merged.Deleted + result.Deleted
                };
                options.Progress?.Invoke(new BulkProgress(written, entities.Count));
            }
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        return kind is BulkOperationKind.Merge or BulkOperationKind.Synchronize
            ? merged
            : Result(kind, written);
    }

    /// <summary>
    ///     Inserts everything reachable from <paramref name="roots" />, principals first.
    /// </summary>
    /// <remarks>
    ///     The whole graph goes in one transaction. A half-written graph would leave dependents
    ///     referencing principals that do not exist, which is worse than not starting.
    /// </remarks>
    public static async Task<BulkResult> InsertGraphAsync<TEntity>(
        DbContext context,
        IReadOnlyList<TEntity> roots,
        BulkOperationOptions options,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (roots.Count == 0)
        {
            return BulkResult.ForInsert(0);
        }

        var bulkOptions = Resolve<BulkOptions>(context, "UseBulkOperations()");
        var executor = Resolve<IBulkOperationExecutor>(context, "UseBulkOperations()");
        var connection = context.GetService<IRelationalConnection>();

        var rootType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new BulkNotSupportedException(
                $"'{typeof(TEntity).Name}' is not part of the model for "
                + $"'{context.GetType().Name}'.");

        var byType = EntityGraphCollector.Collect(context.Model, rootType, roots);
        var order = EntityTypeGraph.TopologicalOrder(context.Model);
        var batchSize = options.BatchSize ?? bulkOptions.MaxBatchSize;

        var total = byType.Sum(kv => kv.Value.Count);

        var written = await BulkTransaction
            .RunAsync(
                context,
                options.Savepoint,
                async ct => await WriteGraphAsync(
                        executor, connection, byType, order, options, batchSize, total, ct)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var (_, entities) in byType)
        {
            Reconcile(context, entities, EntityState.Added, options.Track);
        }

        return BulkResult.ForInsert(written);
    }

    private static async Task<int> WriteGraphAsync(
        IBulkOperationExecutor executor,
        IRelationalConnection connection,
        Dictionary<IEntityType, List<object>> byType,
        IReadOnlyList<IEntityType> order,
        BulkOperationOptions options,
        int batchSize,
        int total,
        CancellationToken cancellationToken)
    {
        var written = 0;

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var entityType in order)
            {
                if (!byType.TryGetValue(entityType, out var entities) || entities.Count == 0)
                {
                    continue;
                }

                var plan = BulkEntityPlan.For(entityType, EntityState.Added);
                var graph = EntityGraphPlan.For(entityType);

                foreach (var layer in EntityGraphCollector.LayerBySelfReference(entityType, entities))
                {
                    // Principals in earlier types and layers now have their keys, so the foreign
                    // keys pointing at them can be filled in. This is the step change tracking
                    // would normally perform.
                    foreach (var entity in layer)
                    {
                        foreach (var fixup in graph.Fixups)
                        {
                            fixup.Apply(entity);
                        }
                    }

                    written += await WriteAsync(
                            executor, connection, plan, layer, batchSize, options.Timeout,
                            cancellationToken)
                        .ConfigureAwait(false);

                    options.Progress?.Invoke(new BulkProgress(written, total));
                }
            }
        }
        finally
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }

        return written;
    }

    private static async Task<int> WriteAsync(
        IBulkOperationExecutor executor,
        IRelationalConnection connection,
        BulkEntityPlan plan,
        List<object> entities,
        int batchSize,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var written = 0;

        for (var offset = 0; offset < entities.Count; offset += batchSize)
        {
            var slice = entities.GetRange(offset, Math.Min(batchSize, entities.Count - offset));
            var rows = new EntityRowSet(
                slice, plan, EntityState.Added, BulkOperationKind.Insert, MergeCounts.Exact,
                timeout);

            if (!executor.CanExecute(rows, out var reason))
            {
                throw new BulkNotSupportedException(
                    $"EF.Toolkit.Bulk cannot insert '{plan.TableName}' as part of a graph: "
                    + $"{reason ?? "no reason given."} Graph inserts cannot fall back per entity "
                    + "type, because the rows already written would be left behind.");
            }

            var result = await executor.ExecuteAsync(rows, connection, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Handled)
            {
                throw new BulkNotSupportedException(
                    $"EF.Toolkit.Bulk cannot insert '{plan.TableName}' as part of a graph: "
                    + $"{result.DeclinedReason ?? "no reason given."}");
            }

            written += result.RowsAffected;
        }

        return written;
    }

    private static BulkResult Result(BulkOperationKind kind, int rows)
        => kind switch
        {
            BulkOperationKind.Insert => new BulkResult { Inserted = rows },
            BulkOperationKind.Update => new BulkResult { Updated = rows },
            BulkOperationKind.Delete => new BulkResult { Deleted = rows },
            // A merge reports its own split; this is only reached for the empty-input shortcut.
            _ => new BulkResult()
        };

    /// <summary>
    ///     Runs the insert through stock EF Core when the provider cannot accelerate it.
    /// </summary>
    /// <remarks>
    ///     Failing outright would be defensible — the caller did ask for a bulk insert — but a
    ///     slow success is easier to live with than an exception in production, and it keeps the
    ///     guarantee that enabling EF.Toolkit.Bulk never breaks anything. The reason is surfaced as a
    ///     diagnostic so it is discoverable rather than silent.
    /// </remarks>
    private static async Task<BulkResult> FallBackAsync<TEntity>(
        DbContext context,
        IReadOnlyList<TEntity> entities,
        EntityState state,
        BulkOperationKind kind,
        string? reason,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        if (kind is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            // Unlike the other verbs there is no stock EF equivalent to fall back to: deciding
            // insert-versus-update per row is exactly the work the database was being asked to do.
            throw new BulkNotSupportedException(
                $"EF.Toolkit.Bulk cannot {kind.ToString().ToLowerInvariant()} '{typeof(TEntity).Name}' on "
                + "this database: "
                + (reason ?? "no reason given."));
        }

        Diagnostics.BulkDiagnostics.ReportExplicitFallback(typeof(TEntity), entities.Count, reason);

        var set = context.Set<TEntity>();
        switch (state)
        {
            case EntityState.Added:
                set.AddRange(entities);
                break;
            case EntityState.Modified:
                set.UpdateRange(entities);
                break;
            case EntityState.Deleted:
                set.RemoveRange(entities);
                break;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Reconciling is the caller's last step, once the surrounding transaction has committed.
        return Result(kind, entities.Count);
    }

    /// <summary>
    ///     Leaves the change tracker in the state the caller asked for.
    /// </summary>
    /// <remarks>
    ///     Entities the context is already tracking are reconciled to <c>Unchanged</c> whether or
    ///     not tracking was requested. An entity still sitting in the tracker as <c>Added</c> after
    ///     its row has been written would be inserted a second time by the next
    ///     <c>SaveChanges()</c>, which is the one genuinely dangerous outcome here.
    ///     <para>
    ///         The already-tracked set is read once from the tracker rather than by calling
    ///         <c>Entry()</c> per entity, which would itself create an entry for every row and give
    ///         back the saving this API exists to provide.
    ///     </para>
    /// </remarks>
    private static void Reconcile<TEntity>(
        DbContext context,
        IReadOnlyList<TEntity> entities,
        EntityState state,
        bool track)
        where TEntity : class
    {
        // A deleted row has nothing left to track, so the entity is released either way; leaving
        // it Unchanged would claim a row exists that does not.
        var settled = state == EntityState.Deleted ? EntityState.Detached : EntityState.Unchanged;

        if (track && state != EntityState.Deleted)
        {
            foreach (var entity in entities)
            {
                context.Entry(entity).State = settled;
            }

            return;
        }

        var tracked = context.ChangeTracker.Entries<TEntity>().ToList();
        if (tracked.Count == 0)
        {
            return;
        }

        var known = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var entry in tracked)
        {
            known.Add(entry.Entity);
        }

        foreach (var entity in entities)
        {
            if (known.Contains(entity))
            {
                context.Entry(entity).State = settled;
            }
        }
    }

    private static List<object> Slice<TEntity>(IReadOnlyList<TEntity> entities, int offset, int count)
        where TEntity : class
    {
        var slice = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            slice.Add(entities[offset + i]);
        }

        return slice;
    }

    private static T Resolve<T>(DbContext context, string call) where T : class
        => context.GetService<T>()
            ?? throw new InvalidOperationException(
                $"EF.Toolkit.Bulk is not configured for this context. Add {call} alongside your provider, "
                + "for example: options.UseNpgsql(connectionString).UseBulkOperations().");
}
