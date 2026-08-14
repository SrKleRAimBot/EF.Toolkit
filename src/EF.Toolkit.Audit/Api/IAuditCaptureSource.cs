using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Api;

/// <summary>
///     A set of changes to one entity type, in a form <see cref="IAuditEntryFactory" /> can turn
///     into audit entries.
/// </summary>
/// <remarks>
///     <para>
///         Column-oriented rather than a list of per-row dictionaries, because the same shape has to
///         serve both a five-row <c>SaveChanges</c> and a hundred-thousand-row bulk operation. A
///         dictionary per row is unremarkable at the first size and ruinous at the second.
///     </para>
///     <para>
///         This is the seam that lets auditing capture writes it knows nothing about. Anything able
///         to describe what it changed — the change tracker, the explicit bulk API, a hand-written
///         import — can produce audit entries indistinguishable from the ones
///         <c>SaveChanges</c> produces, which is what makes the trail uniform across write paths.
///     </para>
/// </remarks>
public interface IAuditCaptureSource
{
    /// <summary>The entity type that changed.</summary>
    IEntityType EntityType { get; }

    /// <summary>What happened to the rows.</summary>
    AuditOperation Operation { get; }

    /// <summary>
    ///     Which write path produced these changes — <c>SaveChanges</c>, <c>Bulk.Update</c>.
    /// </summary>
    string Source { get; }

    /// <summary>
    ///     The properties whose values this source can supply, in the order
    ///     <c>GetCurrentValue</c> and <c>GetOriginalValue</c> index them.
    /// </summary>
    /// <remarks>
    ///     Everything the source has, not everything that will be recorded. Exclusions and masks are
    ///     applied downstream, where the model's configuration is read.
    /// </remarks>
    IReadOnlyList<IProperty> Properties { get; }

    /// <summary>How many rows changed.</summary>
    int RowCount { get; }

    /// <summary>
    ///     Whether <see cref="GetOriginalValue" /> returns the values as they were before the change.
    /// </summary>
    /// <remarks>
    ///     <see langword="false" /> where there is no before-image to read — an insert, or a bulk
    ///     update whose caller turned capture off. An update from such a source records new values
    ///     only, and says so in its payload rather than presenting the new value as if it were both.
    /// </remarks>
    bool HasOriginalValues { get; }

    /// <summary>The value after the change.</summary>
    /// <param name="row">Row index, below <see cref="RowCount" />.</param>
    /// <param name="propertyIndex">Index into <see cref="Properties" />.</param>
    object? GetCurrentValue(int row, int propertyIndex);

    /// <summary>
    ///     The value before the change, when <see cref="HasOriginalValues" /> is
    ///     <see langword="true" />.
    /// </summary>
    /// <param name="row">Row index, below <see cref="RowCount" />.</param>
    /// <param name="propertyIndex">Index into <see cref="Properties" />.</param>
    object? GetOriginalValue(int row, int propertyIndex);

    /// <summary>The changed entity, when one exists.</summary>
    /// <param name="row">Row index, below <see cref="RowCount" />.</param>
    /// <remarks>
    ///     Used for nothing that affects the payload. A source with no entity instances to hand —
    ///     a set-based import, say — may return <see langword="null" />.
    /// </remarks>
    object? GetEntity(int row);

    /// <summary>
    ///     The tenant this row belongs to, read from the configured tenant property.
    /// </summary>
    /// <param name="row">Row index, below <see cref="RowCount" />.</param>
    /// <remarks>
    ///     Separate from <see cref="Properties" /> because the tenant is commonly a shadow property,
    ///     which the change tracker can read and an entity instance cannot. Returning
    ///     <see langword="null" /> falls back to the configured tenant provider.
    /// </remarks>
    string? GetTenantId(int row);
}
