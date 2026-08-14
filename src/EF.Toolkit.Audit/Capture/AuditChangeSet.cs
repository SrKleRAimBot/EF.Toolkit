using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Works out what a pending save is about to change, and snapshots it.
/// </summary>
internal static class AuditChangeSet
{
    /// <summary>Builds one capture source per entity type and operation.</summary>
    /// <param name="context">The context being saved.</param>
    /// <param name="options">The context's auditing settings.</param>
    /// <returns>The sources, or an empty list when nothing being saved is audited.</returns>
    public static List<ChangeTrackerCaptureSource> Build(DbContext context, AuditOptions options)
    {
        Dictionary<(IEntityType, AuditOperation), List<EntityEntry>>? grouped = null;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var plan = AuditEntityPlan.For(entry.Metadata, options);

            if (!plan.IsAudited)
            {
                // Owned parts that share their owner's table are reached through the owner below,
                // and everything else here is genuinely not audited.
                continue;
            }

            if (Operation(entry, plan) is not { } operation || !plan.Audits(operation))
            {
                continue;
            }

            grouped ??= [];

            if (!grouped.TryGetValue((entry.Metadata, operation), out var entries))
            {
                grouped[(entry.Metadata, operation)] = entries = [];
            }

            entries.Add(entry);
        }

        if (grouped is null)
        {
            return [];
        }

        var sources = new List<ChangeTrackerCaptureSource>(grouped.Count);

        foreach (var ((entityType, operation), entries) in grouped)
        {
            sources.Add(
                ChangeTrackerCaptureSource.Create(
                    entityType, operation, entries, AuditEntityPlan.For(entityType, options)));
        }

        return sources;
    }

    /// <summary>
    ///     What is happening to this row, counting changes to the owned values inside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An owned reference that shares its owner's table is part of the owner's row, but EF
    ///         tracks it as an entry of its own. Change only <c>order.Address.City</c> and the
    ///         <c>Address</c> entry is Modified while the <c>Order</c> entry stays Unchanged — so
    ///         looking at the owner's state alone would miss a change to the owner's row entirely.
    ///     </para>
    ///     <para>
    ///         Reached from the owner rather than from the owned entry because EF exposes a
    ///         navigation from principal to dependent and not the reverse, and because the owner is
    ///         what the audit entry is about either way. Unchanged entries are only examined for
    ///         types that actually have folds, so a change tracker full of read-only entities costs
    ///         nothing.
    ///     </para>
    /// </remarks>
    private static AuditOperation? Operation(EntityEntry entry, AuditEntityPlan plan)
    {
        switch (entry.State)
        {
            case EntityState.Added:
                return AuditOperation.Insert;

            case EntityState.Modified:
                return AuditOperation.Update;

            case EntityState.Deleted:
                return AuditOperation.Delete;

            case EntityState.Unchanged when plan.OwnedFolds.Count > 0:
                return OwnedChanged(entry, plan) ? AuditOperation.Update : null;

            default:
                return null;
        }
    }

    private static bool OwnedChanged(EntityEntry owner, AuditEntityPlan plan)
    {
        foreach (var fold in plan.OwnedFolds)
        {
            var entry = owner;
            var reached = true;

            foreach (var navigation in fold.Path)
            {
                if (entry.Reference(navigation.Name).TargetEntry is not { } target)
                {
                    reached = false;
                    break;
                }

                entry = target;
            }

            if (reached && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                return true;
            }
        }

        return false;
    }
}
