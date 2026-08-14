using System.Linq.Expressions;
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

        RefuseMatchOnAnInsert(kind, options);

        var plan = BulkEntityPlan.For(
            entityType,
            state,
            matchProperties,
            BulkProjection.Resolve<TEntity>(entityType, options, kind));

        var scope = Scope<TEntity>(context, entityType, kind, options);

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
                        context, entities, state, kind, plan, scope, executor, connection,
                        batchSize, mergeCounts, options, ct)
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
        BulkScope? scope,
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
                var rows = new EntityRowSet(
                    slice, plan, state, kind, mergeCounts, options.Timeout, scope);

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

        var rootType = context.Model.FindEntityType(typeof(TEntity))
            ?? throw new BulkNotSupportedException(
                $"'{typeof(TEntity).Name}' is not part of the model for "
                + $"'{context.GetType().Name}'.");

        RefuseWhatAGraphCannotHonour<TEntity>(context, rootType, options);

        var bulkOptions = Resolve<BulkOptions>(context, "UseBulkOperations()");
        var executor = Resolve<IBulkOperationExecutor>(context, "UseBulkOperations()");
        var connection = context.GetService<IRelationalConnection>();

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

    /// <summary>
    ///     Refuses the options a graph insert cannot honour, rather than quietly ignoring them.
    /// </summary>
    /// <remarks>
    ///     A graph insert reaches none of the single-type validation, so without this it accepted
    ///     every option and silently applied none of them — worst of all on the two whose entire job
    ///     is to hold something back. The scope and projection resolvers are called here for their
    ///     refusals rather than reimplemented, so a graph insert refuses on exactly the same terms,
    ///     and in the same words, as the insert next to it.
    /// </remarks>
    private static void RefuseWhatAGraphCannotHonour<TEntity>(
        DbContext context,
        IEntityType rootType,
        BulkOperationOptions options)
        where TEntity : class
    {
        if (options.Include is not null || options.Exclude is not null)
        {
            // A graph insert writes several entity types, and a projection is expressed over one of
            // them. Applying it to the root alone and silently writing every column of everything
            // else would be the worst of both readings.
            throw new BulkNotSupportedException(
                "Include and Exclude cannot be combined with IncludeGraph(): a graph insert writes "
                + $"every entity type reachable from '{typeof(TEntity).Name}', and a projection "
                + "over one of them says nothing about the rest.");
        }

        RefuseMatchOnAnInsert(BulkOperationKind.Insert, options);

        // InsertOnly and WithinScope, refused for being an insert rather than for being a graph.
        BulkProjection.Resolve<TEntity>(rootType, options, BulkOperationKind.Insert);
        Scope<TEntity>(context, rootType, BulkOperationKind.Insert, options);
    }

    /// <summary>
    ///     Refuses a <c>MatchOn</c> on an insert, which locates nothing.
    /// </summary>
    /// <remarks>
    ///     An insert writes every row it is handed and looks for none of them, so match columns have
    ///     nothing to find. Taking them and then ignoring them would leave a caller believing they
    ///     had asked for an upsert.
    /// </remarks>
    private static void RefuseMatchOnAnInsert(BulkOperationKind kind, BulkOperationOptions options)
    {
        if (kind != BulkOperationKind.Insert || options.Match is null)
        {
            return;
        }

        throw new BulkNotSupportedException(
            "MatchOn has no meaning for an insert: every row is written, and none is looked for. "
            + "It applies to a merge, a synchronise, an update and a delete — the operations that "
            + "have to find an existing row.");
    }

    /// <summary>
    ///     Translates the call's scope, if it has one, against the provider's identifier rules.
    /// </summary>
    /// <remarks>
    ///     Translated here rather than inside the executor so the two providers share one
    ///     translator: the only provider-specific part is how an identifier is delimited, which
    ///     <see cref="ISqlGenerationHelper" /> already answers. The <c>t</c> alias is the one both
    ///     the synchronise statements give the target table.
    /// </remarks>
    private static BulkScope? Scope<TEntity>(
        DbContext context,
        IEntityType entityType,
        BulkOperationKind kind,
        BulkOperationOptions options)
        where TEntity : class
    {
        if (options.Scope is null && options.ScopeSql is null)
        {
            return null;
        }

        if (kind != BulkOperationKind.Synchronize)
        {
            // Nothing else reaches a row the caller did not hand over, so a scope here has nothing
            // to fence and would silently do nothing at all -- on the one setting whose entire job
            // is to stop a delete going too wide.
            throw new BulkNotSupportedException(
                $"WithinScope applies to BulkSynchronizeAsync, not to {kind.Describe()}. Only a "
                + "synchronise deletes rows its source never named, so only a synchronise has "
                + "anything to confine.");
        }

        if (options.ScopeSql is { } sql)
        {
            return BulkScopePredicate.Translate(sql);
        }

        return options.Scope is Expression<Func<TEntity, bool>> predicate
            ? BulkScopePredicate.Translate(
                entityType, predicate, context.GetService<ISqlGenerationHelper>(), alias: "t")
            : null;
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
