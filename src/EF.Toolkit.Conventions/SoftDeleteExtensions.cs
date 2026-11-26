using System.Linq.Expressions;
using System.Reflection;
using EFToolkit.Conventions;
using EFToolkit.Conventions.Configuration;
using EFToolkit.Conventions.Metadata;
using EFToolkit.Conventions.Saving;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Reading past, restoring and really deleting soft-deleted rows.</summary>
public static class SoftDeleteExtensions
{
    // ---------------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------------

    /// <summary>Includes soft-deleted rows, leaving every other named query filter in force.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The query.</param>
    /// <returns>The query, with the soft-delete filter switched off.</returns>
    public static IQueryable<TEntity> IncludeDeleted<TEntity>(this IQueryable<TEntity> source)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.IgnoreQueryFilters([SoftDeleteFilter.Name]);
    }

    /// <summary>Only the soft-deleted rows — a recycle bin.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="set">The set. Its entity type must be soft-deletable.</param>
    /// <returns>A query over the deleted rows, with every other named filter still in force.</returns>
    public static IQueryable<TEntity> OnlyDeleted<TEntity>(this DbSet<TEntity> set)
        where TEntity : class
    {
        var mapping = Mapping(set);
        return set.IncludeDeleted().Where(
            ConventionMetadata.IsDeleted<TEntity>(mapping.Property.Name, mapping.Property.ClrType));
    }

    /// <summary>Whether <paramref name="entity" /> is marked deleted, by the values it carries.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="entity">The entity. Need not be tracked.</param>
    /// <returns><see langword="true" /> if its soft-delete property says deleted.</returns>
    public static bool IsSoftDeleted<TEntity>(this DbContext context, TEntity entity)
        where TEntity : class
    {
        var entry = Entry(context, entity);
        var mapping = Mapping(entry.Metadata);
        var value = entry.Property(mapping.Property.Name).CurrentValue;
        return mapping.Kind == SoftDeleteKind.Flag ? value is true : value is not null;
    }

    // ---------------------------------------------------------------------------------------
    // Change-tracked writes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     Marks <paramref name="entity" /> to be restored by the next save: its soft-delete property is
    ///     cleared, and an untracked entity is attached.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="entity">The entity — typically read through <see cref="OnlyDeleted{TEntity}" />.</param>
    /// <returns>The entity's entry.</returns>
    public static EntityEntry<TEntity> Restore<TEntity>(this DbContext context, TEntity entity)
        where TEntity : class
    {
        var entry = Entry(context, entity);
        var mapping = Mapping(entry.Metadata);

        // A Restore() cancels a HardDelete() too: left marked, a later ordinary Remove() of the same
        // instance would delete the row for real instead of marking it.
        if (context.GetService<IDbContextOptions>().FindExtension<ConventionsOptionsExtension>() is not null)
        {
            context.GetService<SoftDeleteState>().ClearHardDelete(entity);
        }

        if (entry.State == EntityState.Detached)
        {
            entry.State = EntityState.Unchanged;
        }
        else if (entry.State == EntityState.Deleted)
        {
            // A Remove() and a Restore() before one save cancel out — including the foreign keys of
            // loaded optional dependents Remove() nulled under Immediate cascade timing.
            entry.State = EntityState.Unchanged;
            if (context.ChangeTracker.CascadeDeleteTiming == CascadeTiming.Immediate)
            {
                CascadeRepair.RestoreNulledForeignKeys(context, [entry]);
            }
        }

        // Remove() marked the owned parts deleted along with the entity; undoing it undoes theirs.
        OwnedEntries.Keep(entry);

        var property = entry.Property(mapping.Property.Name);
        property.CurrentValue = ConventionMetadata.LiveValue(mapping);
        property.IsModified = true;
        return entry;
    }

    /// <summary>
    ///     Removes <paramref name="entity" /> so that the next save deletes its row for real, bypassing
    ///     soft delete. For erasure requests, and for purging rows long since soft-deleted.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context. Must be configured with <c>UseEntityConventions()</c>.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The entity's entry, now <see cref="EntityState.Deleted" />.</returns>
    public static EntityEntry<TEntity> HardDelete<TEntity>(this DbContext context, TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);

        var state = context.GetService<IDbContextOptions>().FindExtension<ConventionsOptionsExtension>() is null
            ? null
            : context.GetService<SoftDeleteState>();

        // Without the conventions configured, Remove() already deletes; marking is just bookkeeping.
        state?.MarkHardDelete(entity);
        return context.Remove(entity);
    }

    // ---------------------------------------------------------------------------------------
    // Set-based writes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    ///     Soft-deletes every live row matching <paramref name="condition" /> in one statement, stamping
    ///     the updated-at timestamp if the type has one.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="set">The set. Its entity type must be soft-deletable.</param>
    /// <param name="condition">Which rows.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows marked deleted.</returns>
    /// <remarks>
    ///     Rows already deleted are not matched — the soft-delete filter still applies — so a
    ///     timestamp records when a row was first deleted, not the latest repeat.
    /// </remarks>
    public static Task<int> SoftDeleteWhereAsync<TEntity>(
        this DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        var setters = Setters(set, deleted: true);
        return set.Where(condition).ExecuteUpdateAsync(setters, cancellationToken);
    }

    /// <inheritdoc cref="SoftDeleteWhereAsync{TEntity}(DbSet{TEntity}, Expression{Func{TEntity, bool}}, CancellationToken)" />
    public static int SoftDeleteWhere<TEntity>(this DbSet<TEntity> set, Expression<Func<TEntity, bool>> condition)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        return set.Where(condition).ExecuteUpdate(Setters(set, deleted: true));
    }

    /// <summary>Restores every soft-deleted row matching <paramref name="condition" /> in one statement.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="set">The set. Its entity type must be soft-deletable.</param>
    /// <param name="condition">Which deleted rows.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of rows restored.</returns>
    public static Task<int> RestoreWhereAsync<TEntity>(
        this DbSet<TEntity> set,
        Expression<Func<TEntity, bool>> condition,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        var setters = Setters(set, deleted: false);
        return set.OnlyDeleted().Where(condition).ExecuteUpdateAsync(setters, cancellationToken);
    }

    /// <inheritdoc cref="RestoreWhereAsync{TEntity}(DbSet{TEntity}, Expression{Func{TEntity, bool}}, CancellationToken)" />
    public static int RestoreWhere<TEntity>(this DbSet<TEntity> set, Expression<Func<TEntity, bool>> condition)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(condition);
        return set.OnlyDeleted().Where(condition).ExecuteUpdate(Setters(set, deleted: false));
    }

    // ---------------------------------------------------------------------------------------

    private static Action<UpdateSettersBuilder<TEntity>> Setters<TEntity>(DbSet<TEntity> set, bool deleted)
        where TEntity : class
    {
        var mapping = Mapping(set);
        var timestamps = ConventionMetadata.FindTimestamps(set.EntityType);
        var clock = set.GetService<IDbContextOptions>().FindExtension<ConventionsOptionsExtension>()?.Options.Clock
            ?? TimeProvider.System;
        var now = clock.GetUtcNow();

        var assignments = new List<(IProperty Property, object? Value)>
        {
            (mapping.Property, deleted ? ConventionMetadata.DeletedValue(mapping, now) : ConventionMetadata.LiveValue(mapping)),
        };

        if (timestamps?.UpdatedAt is { } updatedAt)
        {
            assignments.Add((updatedAt, ConventionMetadata.TimestampValue(updatedAt.ClrType, now)));
        }

        return builder =>
        {
            foreach (var (property, value) in assignments)
            {
                var row = Expression.Parameter(typeof(TEntity), "e");
                var delegateType = typeof(Func<,>).MakeGenericType(typeof(TEntity), property.ClrType);
                var selector = Expression.Lambda(
                    delegateType, ConventionMetadata.PropertyAccess(row, property.Name, property.ClrType), row);
                SetProperty<TEntity>.Definition
                    .MakeGenericMethod(property.ClrType)
                    .Invoke(builder, [selector, value]);
            }
        };
    }

    private static SoftDeleteMapping Mapping<TEntity>(DbSet<TEntity> set)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        return Mapping(set.EntityType);
    }

    private static SoftDeleteMapping Mapping(IEntityType entityType)
        => ConventionMetadata.FindSoftDelete(entityType)
            ?? throw new InvalidOperationException(
                $"'{entityType.DisplayName()}' is not soft-deletable. Configure it with HasSoftDelete(...) "
                + "in OnModelCreating.");

    private static EntityEntry<TEntity> Entry<TEntity>(DbContext context, TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);
        return context.Entry(entity);
    }

    // The (property selector, constant value) overload of SetProperty, found once per entity type. The
    // values here are fixed before the statement runs, so they go in as parameters, not as lambdas.
    private static class SetProperty<TEntity>
    {
        internal static readonly MethodInfo Definition = typeof(UpdateSettersBuilder<TEntity>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == nameof(UpdateSettersBuilder<TEntity>.SetProperty)
                && m.IsGenericMethodDefinition
                && m.GetParameters() is [_, { ParameterType.IsGenericParameter: true }]);
    }
}
