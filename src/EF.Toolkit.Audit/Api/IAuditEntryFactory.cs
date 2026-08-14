namespace EFToolkit.Audit.Api;

/// <summary>
///     Turns described changes into audit entries.
/// </summary>
/// <remarks>
///     <para>
///         Public so that a capture path outside this package — EF.Toolkit.Audit.Bulk, or an
///         application's own — produces entries identical to the ones the change tracker produces.
///         Registration, exclusions, masking, key formatting, payload shape, the actor, the tenant
///         and the clock all live behind here, so there is exactly one implementation of what an
///         audit entry is.
///     </para>
///     <para>
///         Every source in one call belongs to one unit of work: the actor and the correlation id
///         are resolved once and stamped on all of them, so a save touching six entity types
///         produces six sets of entries that correlate.
///     </para>
/// </remarks>
public interface IAuditEntryFactory
{
    /// <summary>Builds the entries for one unit of work.</summary>
    /// <param name="sources">What changed, one source per entity type and operation.</param>
    /// <param name="cancellationToken">Cancels actor and tenant resolution.</param>
    /// <returns>
    ///     The entries, in source order. Empty when nothing in <paramref name="sources" /> is
    ///     audited, or when every update turned out to change nothing.
    /// </returns>
    ValueTask<IReadOnlyList<AuditEntry>> CreateAsync(
        IReadOnlyList<IAuditCaptureSource> sources,
        CancellationToken cancellationToken);

    /// <summary>Whether an entity type produces entries for an operation at all.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="operation">The operation.</param>
    /// <remarks>
    ///     Lets a capture path skip the work of describing a change nobody will record — which for
    ///     the bulk API means skipping an extra read of every row it is about to update.
    /// </remarks>
    bool Audits(Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType, AuditOperation operation);
}
