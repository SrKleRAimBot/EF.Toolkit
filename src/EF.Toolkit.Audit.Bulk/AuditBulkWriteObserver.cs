using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Diagnostics;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Bulk;

/// <summary>
///     Captures audit entries for the explicit bulk API.
/// </summary>
/// <remarks>
///     <para>
///         The explicit bulk API bypasses the change tracker, so the <c>SaveChanges</c> interceptor
///         that audits everything else never sees it. This is the other half: it runs inside the
///         operation's own transaction, after the write and after generated keys have landed, so the
///         entries it produces are atomic with the rows they describe and name real keys.
///     </para>
///     <para>
///         Everything about what an entry <em>is</em> comes from <see cref="IAuditEntryFactory" />,
///         which is also what the interceptor uses. That is deliberate and it is testable: the same
///         logical change made through <c>SaveChanges</c> and through <c>BulkUpdateAsync</c> should
///         produce identical entries, and it does because neither path decides anything for itself.
///     </para>
/// </remarks>
internal sealed class AuditBulkWriteObserver(
    AuditOptions options,
    IAuditEntryFactory factory,
    IAuditSink sink,
    ICurrentDbContext currentContext) : IBulkWriteObserver
{
    /// <inheritdoc />
    public BulkObservationNeeds Observes(IEntityType entityType, BulkOperationKind operation)
    {
        if (!Audits(entityType, operation))
        {
            return BulkObservationNeeds.None;
        }

        // An insert has no earlier state to read, so asking for one would buy a round trip and
        // nothing else.
        var wantsBefore = operation != BulkOperationKind.Insert && options.CaptureBeforeImages;

        return wantsBefore
            ? BulkObservationNeeds.All
            : BulkObservationNeeds.NewValues;
    }

    /// <inheritdoc />
    public async ValueTask ObservedAsync(
        BulkWriteObservation observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var sources = BulkCaptureSource.For(observation);

        if (sources.Count == 0)
        {
            return;
        }

        var entries = await factory.CreateAsync(sources, cancellationToken).ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return;
        }

        var context = currentContext.Context;

        var writeContext = new AuditWriteContext(
            context, context.Database.CurrentTransaction, sources[0].Source);

        try
        {
            await sink.WriteAsync(entries, writeContext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AuditDiagnostics.ReportSinkFailed(sources[0].Source, entries.Count, exception);

            if (options.OnAuditFailure == AuditFailure.Throw)
            {
                // Thrown from inside the operation's transaction, so the write it was describing
                // goes back with it. That is the point of the setting.
                throw;
            }
        }
    }

    /// <summary>
    ///     Whether any of the operations this bulk call can perform produces an entry.
    /// </summary>
    /// <remarks>
    ///     A merge is an insert or an update per row and a synchronise is either plus a delete, so
    ///     the question is asked of every operation the call could make. Which one each row actually
    ///     got is settled later, from the before-image.
    /// </remarks>
    private bool Audits(IEntityType entityType, BulkOperationKind operation)
        => operation switch
        {
            BulkOperationKind.Insert => factory.Audits(entityType, AuditOperation.Insert),
            BulkOperationKind.Update => factory.Audits(entityType, AuditOperation.Update),
            BulkOperationKind.Delete => factory.Audits(entityType, AuditOperation.Delete),
            BulkOperationKind.Merge =>
                factory.Audits(entityType, AuditOperation.Insert)
                || factory.Audits(entityType, AuditOperation.Update),
            _ =>
                factory.Audits(entityType, AuditOperation.Insert)
                || factory.Audits(entityType, AuditOperation.Update)
                || factory.Audits(entityType, AuditOperation.Delete),
        };
}
