using EFToolkit.Conventions.Configuration;
using EFToolkit.Conventions.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Conventions.Saving;

/// <summary>
///     Applies soft delete and timestamps to the change tracker just before a save writes it.
/// </summary>
/// <remarks>
///     <para>
///         The interceptor runs before EF's own change detection, so it detects first: an entity
///         edited without notification would otherwise reach the save as <c>Unchanged</c>, get no
///         timestamp, and then be written by the detection that follows.
///     </para>
///     <para>
///         A soft delete is the subtle half. <c>Remove()</c> has already done EF's cascade work by the
///         time a save starts — owned rows marked deleted with their owner, optional dependents'
///         foreign keys set to null — on the assumption that the principal's row is about to go. When
///         the principal stays, that work is undone too: owned rows are kept, and a foreign key nulled
///         only because its principal was removed is put back. A soft-deletable dependent that the
///         cascade marked deleted is simply soft-deleted alongside.
///     </para>
/// </remarks>
internal sealed class ConventionsSaveChangesInterceptor(ConventionsOptions options, SoftDeleteState state)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Completed(eventData.Context);
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Completed(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Failed();

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Failed();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => Failed();

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        Failed();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------------------------------

    internal void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        if (context.ChangeTracker.AutoDetectChangesEnabled)
        {
            context.ChangeTracker.DetectChanges();
        }

        var now = options.Clock.GetUtcNow();
        state.Converted.Clear();

        if (options.SoftDelete)
        {
            ConvertDeletes(context, now);
        }

        if (options.Timestamps)
        {
            Stamp(context, now);
        }
    }

    private void ConvertDeletes(DbContext context, DateTimeOffset now)
    {
        var entries = context.ChangeTracker.Entries().ToList();

        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Deleted || state.IsHardDelete(entry.Entity))
            {
                continue;
            }

            var mapping = ConventionMetadata.FindSoftDelete(entry.Metadata);
            if (mapping is null)
            {
                continue;
            }

            var property = entry.Property(mapping.Property.Name);
            var before = property.CurrentValue;

            // Unchanged first, then one modified column: Modified outright would write every column,
            // and a deleted entity's values are whatever the application last left on it.
            // Edits made before the Remove() would otherwise ride along: EF's own change detection,
            // which runs after this, would find them on an entry that is now Modified. A real delete
            // discards them, so they are accepted as the baseline instead — all but concurrency tokens,
            // whose original value is what the UPDATE's WHERE clause has to check.
            entry.State = EntityState.Unchanged;
            foreach (var other in entry.Properties)
            {
                if (!other.Metadata.IsConcurrencyToken
                    && !other.Metadata.IsPrimaryKey()
                    && !Equals(other.CurrentValue, other.OriginalValue))
                {
                    other.OriginalValue = other.CurrentValue;
                }

                other.IsModified = false;
            }

            property.CurrentValue = ConventionMetadata.DeletedValue(mapping, now);
            property.IsModified = true;

            state.Converted.Add((entry, mapping.Property.Name, before));
        }

        if (state.Converted.Count == 0)
        {
            return;
        }

        foreach (var (converted, _, _) in state.Converted)
        {
            OwnedEntries.Keep(converted);
        }

        // Under the default Immediate timing, Remove() has already nulled the foreign keys of loaded
        // optional dependents, and a null left by that fixup looks exactly like one the application set.
        // They are restored, since the principal is staying. Under OnSaveChanges nothing has cascaded
        // before this point — EF's own cascade then finds the principal no longer deleted — so every
        // null key is the application's, and none is touched.
        if (context.ChangeTracker.CascadeDeleteTiming != CascadeTiming.Immediate)
        {
            return;
        }

        CascadeRepair.RestoreNulledForeignKeys(context, state.Converted.Select(c => c.Entry).ToList());
    }

    private static void Stamp(DbContext context, DateTimeOffset now)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            var mapping = ConventionMetadata.FindTimestamps(entry.Metadata);
            if (mapping is null)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                // A value the application set — an import, a seed with fixed times — is kept. Only
                // a property still at its default is stamped.
                StampIfDefault(entry, mapping.CreatedAt, now);
                StampIfDefault(entry, mapping.UpdatedAt, now);
                continue;
            }

            if (mapping.CreatedAt is not null)
            {
                var created = entry.Property(mapping.CreatedAt.Name);
                if (created.IsModified)
                {
                    // When a row was created is a fact about the past; a save does not get to edit it.
                    created.CurrentValue = created.OriginalValue;
                    created.IsModified = false;
                }
            }

            if (mapping.UpdatedAt is not null)
            {
                var updated = entry.Property(mapping.UpdatedAt.Name);
                updated.CurrentValue = ConventionMetadata.TimestampValue(mapping.UpdatedAt.ClrType, now);
                updated.IsModified = true;
            }
        }
    }

    private static void StampIfDefault(EntityEntry entry, IProperty? property, DateTimeOffset now)
    {
        if (property is null)
        {
            return;
        }

        var value = entry.Property(property.Name);
        if (value.CurrentValue is null or DateTime { Ticks: 0 } or DateTimeOffset { Ticks: 0 })
        {
            value.CurrentValue = ConventionMetadata.TimestampValue(property.ClrType, now);
        }
    }

    private void Completed(DbContext? context)
    {
        foreach (var (entry, _, _) in state.Converted)
        {
            // Stock EF detaches an entity once its delete is saved. The row is still there here, but
            // as far as the application is concerned it is gone — the query filter says so — and a
            // tracked instance would let it reappear through identity resolution.
            if (entry.State == EntityState.Unchanged)
            {
                entry.State = EntityState.Detached;
            }
        }

        state.Converted.Clear();

        // A hard-delete mark is spent once its delete has been accepted — keeping it would hard-delete
        // the same instance if it were ever attached and removed again. An entity still Deleted after a
        // successful save, though, is one SaveChanges(acceptAllChangesOnSuccess: false) left pending: the
        // caller may roll back and save again, and that retry must still delete it for real.
        state.ClearHardDeletes(entity => context?.Entry(entity).State == EntityState.Deleted);
    }

    private void Failed()
    {
        // Put the tracker back as the application left it, so that a retried save converts the
        // delete again — with a fresh time — instead of sending a half-converted update.
        foreach (var (entry, property, before) in state.Converted)
        {
            if (entry.State == EntityState.Detached)
            {
                continue;
            }

            var value = entry.Property(property);
            value.CurrentValue = before;
            value.IsModified = false;
            entry.State = EntityState.Deleted;
        }

        state.Converted.Clear();
    }
}
