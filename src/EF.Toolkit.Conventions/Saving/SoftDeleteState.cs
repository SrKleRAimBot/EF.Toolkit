using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Conventions.Saving;

/// <summary>Per-context bookkeeping for soft delete. Scoped to one context instance.</summary>
internal sealed class SoftDeleteState : IResettableService
{
    private readonly HashSet<object> _hardDeletes = new(ReferenceEqualityComparer.Instance);

    /// <summary>The deletes the current save turned into updates, with the value each flag had before.</summary>
    internal List<(EntityEntry Entry, string Property, object? Before)> Converted { get; } = [];

    /// <summary>Conversions a save wrote without accepting, carried to the next save.</summary>
    internal List<(EntityEntry Entry, string Property, object? Before)> Pending { get; } = [];

    private readonly HashSet<object> _propagated = new(ReferenceEqualityComparer.Instance);

    /// <summary>Marks <paramref name="entity" /> to be deleted for real by the next save.</summary>
    internal void MarkHardDelete(object entity) => _hardDeletes.Add(entity);

    /// <summary>
    ///     Whether <paramref name="entity" /> is to be deleted for real: marked with
    ///     <see cref="MarkHardDelete" />, or reached this save by cascade from one that was.
    /// </summary>
    internal bool IsHardDelete(object entity) => _hardDeletes.Contains(entity) || _propagated.Contains(entity);

    /// <summary>Marks a dependent cascaded from a hard-deleted principal, for the current save only.</summary>
    internal void MarkPropagatedHardDelete(object entity) => _propagated.Add(entity);

    /// <summary>Forgets every cascaded mark; each save recomputes them.</summary>
    internal void ClearPropagatedHardDeletes() => _propagated.Clear();

    /// <summary>Forgets a pending, unaccepted soft delete of <paramref name="entity" />.</summary>
    internal void ClearPending(object entity) => Pending.RemoveAll(p => ReferenceEquals(p.Entry.Entity, entity));

    /// <summary>Forgets the hard-delete mark on <paramref name="entity" />, if it has one.</summary>
    internal void ClearHardDelete(object entity) => _hardDeletes.Remove(entity);

    /// <summary>Forgets the hard-delete marks a save has carried out, keeping those <paramref name="stillPending" /> says are not.</summary>
    internal void ClearHardDeletes(Func<object, bool> stillPending) => _hardDeletes.RemoveWhere(entity => !stillPending(entity));

    /// <summary>Clears everything when a pooled context is returned: the next lease starts clean.</summary>
    public void ResetState()
    {
        _hardDeletes.Clear();
        _propagated.Clear();
        Converted.Clear();
        Pending.Clear();
    }

    /// <inheritdoc cref="ResetState" />
    public Task ResetStateAsync(CancellationToken cancellationToken = default)
    {
        ResetState();
        return Task.CompletedTask;
    }
}
