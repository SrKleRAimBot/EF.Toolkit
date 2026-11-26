using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace EFToolkit.Conventions.Saving;

/// <summary>Per-context bookkeeping for soft delete. Scoped to one context instance.</summary>
internal sealed class SoftDeleteState
{
    private readonly HashSet<object> _hardDeletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The deletes the current save turned into updates, with the value each flag had before.</summary>
    internal List<(EntityEntry Entry, string Property, object? Before)> Converted { get; } = [];

    /// <summary>Marks <paramref name="entity" /> to be deleted for real by the next save.</summary>
    internal void MarkHardDelete(object entity) => _hardDeletes.Add(entity);

    /// <summary>Whether <paramref name="entity" /> was marked with <see cref="MarkHardDelete" />.</summary>
    internal bool IsHardDelete(object entity) => _hardDeletes.Contains(entity);

    /// <summary>Forgets every hard-delete mark, once a save has carried them out.</summary>
    internal void ClearHardDeletes() => _hardDeletes.Clear();
}
