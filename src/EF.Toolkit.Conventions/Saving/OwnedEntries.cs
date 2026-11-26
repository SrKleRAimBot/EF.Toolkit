using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Conventions.Saving;

/// <summary>Walks the owned entities beneath an owner, however deeply nested.</summary>
internal static class OwnedEntries
{
    /// <summary>
    ///     Returns every <see cref="EntityState.Deleted" /> entity owned, directly or through other
    ///     owned entities, by <paramref name="owner" /> to <see cref="EntityState.Unchanged" />.
    /// </summary>
    /// <remarks>
    ///     <c>Remove()</c> marks a whole ownership subtree deleted with its owner. When the owner's row
    ///     stays, so must every row in that subtree: a table-split owned type would otherwise have its
    ///     columns nulled, and an owned collection in a table of its own would lose its rows. Only
    ///     ownership navigations are followed, owner to owned, so a nested owned type's navigation back
    ///     to its owner is never walked.
    /// </remarks>
    internal static void Keep(EntityEntry owner)
    {
        foreach (var navigation in owner.Navigations)
        {
            if (navigation.Metadata is not INavigation { IsOnDependent: false, ForeignKey.IsOwnership: true }
                || navigation.CurrentValue is null)
            {
                continue;
            }

            var owned = navigation.Metadata.IsCollection
                ? ((System.Collections.IEnumerable)navigation.CurrentValue).Cast<object>()
                : [navigation.CurrentValue];

            foreach (var value in owned)
            {
                var entry = owner.Context.Entry(value);
                if (entry.State == EntityState.Deleted)
                {
                    entry.State = EntityState.Unchanged;
                }

                Keep(entry);
            }
        }
    }
}
