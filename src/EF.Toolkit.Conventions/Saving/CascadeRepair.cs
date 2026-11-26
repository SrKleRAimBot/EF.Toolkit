using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Conventions.Saving;

/// <summary>Undoes the foreign-key fixup <c>Remove()</c> applied to the dependents of a principal that stays.</summary>
/// <remarks>
///     Under EF's default <c>CascadeTiming.Immediate</c>, <c>Remove()</c> sets the foreign keys of loaded
///     optional dependents to <see langword="null" /> at once. When the principal's row is going to stay —
///     soft-deleted, or restored before the save — those keys are put back. Callers check the timing:
///     under <c>OnSaveChanges</c> no fixup has happened, and every null key is the application's own.
/// </remarks>
internal static class CascadeRepair
{
    /// <summary>
    ///     Restores every key in <paramref name="context" /> that was nulled while pointing at one of
    ///     <paramref name="principals" />.
    /// </summary>
    internal static void RestoreNulledForeignKeys(DbContext context, IReadOnlyList<EntityEntry> principals)
    {
        if (principals.Count == 0)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.State == EntityState.Modified)
            {
                Restore(entry, principals);
            }
        }
    }

    private static void Restore(EntityEntry entry, IReadOnlyList<EntityEntry> principals)
    {
        foreach (var foreignKey in entry.Metadata.GetForeignKeys())
        {
            // ClientNoAction is the one behaviour under which EF's delete fixup never touches the
            // dependent's key, so a null there was set by the application and is not ours to undo.
            if (foreignKey.IsOwnership || foreignKey.DeleteBehavior == DeleteBehavior.ClientNoAction)
            {
                continue;
            }

            var properties = foreignKey.Properties.Select(p => entry.Property(p.Name)).ToList();
            if (!properties.All(p => p.IsModified && p.CurrentValue is null && p.OriginalValue is not null))
            {
                continue;
            }

            var principal = Find(foreignKey, properties.Select(p => p.OriginalValue).ToArray(), principals);
            if (principal is null)
            {
                continue;
            }

            // The key was nulled by Remove()'s fixup, not by the application: the principal it
            // pointed at is staying. Re-pointing the navigation first lets EF's own fixup restore the
            // collection on the principal side; resetting the values afterwards undoes the column.
            if (foreignKey.DependentToPrincipal is { } navigation)
            {
                entry.Reference(navigation.Name).CurrentValue = principal.Entity;
            }

            foreach (var property in properties)
            {
                property.CurrentValue = property.OriginalValue;
                property.IsModified = false;
            }

            if (entry.State == EntityState.Modified
                && !entry.Properties.Any(p => p.IsModified)
                && !entry.ComplexProperties.Any(p => p.IsModified))
            {
                entry.State = EntityState.Unchanged;
            }
        }
    }

    /// <summary>The principal among <paramref name="principals" /> whose key is <paramref name="values" />.</summary>
    internal static EntityEntry? Find(IForeignKey foreignKey, object?[] values, IEnumerable<EntityEntry> principals)
    {
        foreach (var candidate in principals)
        {
            if (!foreignKey.PrincipalEntityType.IsAssignableFrom(candidate.Metadata))
            {
                continue;
            }

            var key = foreignKey.PrincipalKey.Properties;
            var matches = true;
            for (var i = 0; i < key.Count && matches; i++)
            {
                matches = Equals(candidate.Property(key[i].Name).CurrentValue, values[i]);
            }

            if (matches)
            {
                return candidate;
            }
        }

        return null;
    }
}
