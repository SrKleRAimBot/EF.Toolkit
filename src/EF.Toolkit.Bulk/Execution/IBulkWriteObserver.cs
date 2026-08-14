using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Sees what an explicit bulk operation wrote, inside the transaction that wrote it.
/// </summary>
/// <remarks>
///     <para>
///         The explicit bulk API bypasses the change tracker entirely, which is where its advantage
///         comes from and also why <c>ISaveChangesInterceptor</c> never fires for it. Anything that
///         needs to react to a write — an outbox, a cache invalidation, an audit trail — therefore
///         has nowhere to stand. This is that place.
///     </para>
///     <para>
///         Observers run after the write has succeeded and after store-generated values have been
///         written back, but before the transaction commits. Work done here is atomic with the write
///         it describes, and throwing from here rolls the write back.
///     </para>
///     <para>
///         Register one in EF Core's internal service provider from an
///         <see cref="Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsExtension" />.
///         When none is registered the cost is a single null check, so an application that does not
///         use this pays nothing for it.
///     </para>
/// </remarks>
public interface IBulkWriteObserver
{
    /// <summary>What this observer wants to see for a given write, if anything.</summary>
    /// <param name="entityType">The entity type being written.</param>
    /// <param name="operation">What is being done to it.</param>
    /// <remarks>
    ///     Asked once per operation, before any work is done, so that an observer with no interest
    ///     in this entity type costs nothing — and, crucially, so that the extra read a before-image
    ///     needs is only issued when something is actually going to use it.
    /// </remarks>
    BulkObservationNeeds Observes(IEntityType entityType, BulkOperationKind operation);

    /// <summary>Reports what was written.</summary>
    /// <param name="observation">The rows, their values, and their before-images if requested.</param>
    /// <param name="cancellationToken">Cancels the observer's own work.</param>
    ValueTask ObservedAsync(BulkWriteObservation observation, CancellationToken cancellationToken);
}

/// <summary>
///     What an <see cref="IBulkWriteObserver" /> needs from a write.
/// </summary>
[Flags]
public enum BulkObservationNeeds
{
    /// <summary>Nothing. The observer is not interested in this write.</summary>
    None = 0,

    /// <summary>The values that were written, and the entities they came from.</summary>
    NewValues = 1,

    /// <summary>
    ///     The rows as they stood before the write.
    /// </summary>
    /// <remarks>
    ///     Costs one extra read of the affected rows, issued inside the same transaction and joined
    ///     to the staging table the operation already built. Nothing to ask for on an insert, where
    ///     there is no earlier state to read.
    /// </remarks>
    BeforeImages = 2,

    /// <summary>Both.</summary>
    All = NewValues | BeforeImages,
}
