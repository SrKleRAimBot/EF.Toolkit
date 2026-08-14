using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Diagnostics;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Sinks;

/// <summary>
///     Writes audit entries through the context that produced the change. The default.
/// </summary>
/// <typeparam name="TKey">The configured entry key type.</typeparam>
/// <remarks>
///     <para>
///         The same connection and the same transaction, so an entry cannot survive a rolled-back
///         change nor go missing for a committed one. That guarantee is the reason this is the
///         default and the reason anything else has to say
///         <c>Atomicity(AuditAtomicity.BestEffort)</c> out loud.
///     </para>
///     <para>
///         Entries go in through a registered <see cref="IAuditBatchWriter" /> when there are enough
///         of them and one exists, and through <c>AddRange</c> otherwise. Nothing here knows what
///         that writer does — installing EF.Toolkit.Audit.Bulk registers one over
///         <c>BulkInsertAsync</c>, and without it the plain path is entirely correct.
///     </para>
/// </remarks>
internal sealed class SameContextAuditSink<TKey>(
    AuditOptions options,
    IAuditBatchWriter? batchWriter = null) : IAuditSink
{
    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyList<AuditEntry> entries,
        AuditWriteContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);

        var typed = new List<AuditEntry<TKey>>(entries.Count);
        foreach (var entry in entries)
        {
            typed.Add((AuditEntry<TKey>)entry);
        }

        var batched = batchWriter is not null && typed.Count >= options.BatchThreshold;

        if (batched)
        {
            await batchWriter!.WriteAsync(context.Context, typed, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await SaveAsync(context.Context, typed, cancellationToken).ConfigureAwait(false);
        }

        AuditDiagnostics.ReportEntriesWritten(context.Source, typed.Count, batched);
    }

    /// <summary>
    ///     Inserts the entries and lets go of them again.
    /// </summary>
    /// <remarks>
    ///     Detaching afterwards matters more than it looks. The entries are tracked as Unchanged
    ///     once saved, and a long-lived context auditing save after save would otherwise accumulate
    ///     every entry it has ever written — a leak that shows up as a slow change tracker rather
    ///     than as anything obviously wrong.
    /// </remarks>
    private static async Task SaveAsync(
        DbContext context,
        List<AuditEntry<TKey>> entries,
        CancellationToken cancellationToken)
    {
        context.Set<AuditEntry<TKey>>().AddRange(entries);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var entry in entries)
            {
                context.Entry(entry).State = EntityState.Detached;
            }
        }
    }
}
