using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     The observers interested in one explicit bulk operation, and what they asked to see.
/// </summary>
/// <remarks>
///     Resolved once per operation rather than once per batch, and absent entirely when nothing is
///     registered or nothing is interested — which is the common case, and must stay free.
/// </remarks>
internal sealed class BulkObservers
{
    private readonly IBulkWriteObserver[] _observers;

    private BulkObservers(IBulkWriteObserver[] observers, BulkObservationNeeds needs)
    {
        _observers = observers;
        Needs = needs;
    }

    /// <summary>The union of what every interested observer asked for.</summary>
    public BulkObservationNeeds Needs { get; }

    /// <summary>Whether the target rows must be read before they are changed.</summary>
    public bool WantsBeforeImages => Needs.HasFlag(BulkObservationNeeds.BeforeImages);

    /// <summary>
    ///     Finds the observers interested in this operation, or <see langword="null" /> if there are
    ///     none.
    /// </summary>
    /// <param name="context">The context the operation belongs to.</param>
    /// <param name="entityType">The entity type being written.</param>
    /// <param name="operation">What is being done to it.</param>
    /// <param name="options">The per-call options, which may turn observation off.</param>
    public static BulkObservers? For(
        DbContext context,
        IEntityType entityType,
        BulkOperationKind operation,
        BulkOperationOptions options)
    {
        if (!options.Observe)
        {
            return null;
        }

        // IEnumerable<T> always resolves, empty when nothing registered, so this costs one lookup
        // for an application that uses no observers at all.
        var registered = ((IInfrastructure<IServiceProvider>)context).Instance
            .GetService(typeof(IEnumerable<IBulkWriteObserver>)) as IEnumerable<IBulkWriteObserver>;

        if (registered is null)
        {
            return null;
        }

        List<IBulkWriteObserver>? interested = null;
        var needs = BulkObservationNeeds.None;

        foreach (var observer in registered)
        {
            var wanted = observer.Observes(entityType, operation);

            if (wanted == BulkObservationNeeds.None)
            {
                continue;
            }

            (interested ??= []).Add(observer);
            needs |= wanted;
        }

        if (interested is null)
        {
            return null;
        }

        if (operation == BulkOperationKind.Insert || options.CaptureBeforeImages == false)
        {
            // Nothing precedes an insert, and a caller may decline the extra read on anything else.
            needs &= ~BulkObservationNeeds.BeforeImages;
        }

        return new BulkObservers([.. interested], needs);
    }

    /// <summary>Somewhere for the executor to record the rows as they stand, or null.</summary>
    /// <param name="entityType">The entity type being written.</param>
    /// <param name="rowCount">How many rows this batch covers.</param>
    public BulkBeforeImages? Capture(IEntityType entityType, int rowCount)
        => WantsBeforeImages
            ? new BulkBeforeImages(BulkBeforeImages.ColumnsFor(entityType), rowCount)
            : null;

    /// <summary>Reports one batch to every interested observer.</summary>
    /// <param name="entityType">The entity type that was written.</param>
    /// <param name="rows">The batch.</param>
    /// <param name="entities">The entities behind it.</param>
    /// <param name="beforeImages">The rows as they stood, when they were captured.</param>
    /// <param name="cancellationToken">Cancels the observers' work.</param>
    /// <remarks>
    ///     Called inside the operation's transaction and after generated values have been written
    ///     back, so an observer sees final values and its own work commits or rolls back with the
    ///     write.
    /// </remarks>
    public async ValueTask ObserveAsync(
        IEntityType entityType,
        IBulkRowSet rows,
        IReadOnlyList<object> entities,
        BulkBeforeImages? beforeImages,
        CancellationToken cancellationToken)
    {
        var observation = new BulkWriteObservation(entityType, rows, entities, beforeImages);

        foreach (var observer in _observers)
        {
            await observer.ObservedAsync(observation, cancellationToken).ConfigureAwait(false);
        }
    }
}
