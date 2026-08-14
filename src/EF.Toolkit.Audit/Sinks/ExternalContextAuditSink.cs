using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Audit.Sinks;

/// <summary>
///     Writes audit entries through a dedicated audit context.
/// </summary>
/// <typeparam name="TKey">The configured entry key type.</typeparam>
/// <remarks>
///     <para>
///         For a modular monolith that wants one audit store behind several databases. It is a real
///         trade: the audit context has its own connection and its own transaction, so a change can
///         commit while its entry fails to, and the other way round. That is why configuring this
///         also requires saying <c>Atomicity(AuditAtomicity.BestEffort)</c> — the guarantee is being
///         given up, and it should be given up on purpose.
///     </para>
///     <para>
///         The audit context is resolved per write from the application's service provider, so it
///         follows whatever lifetime the application registered it with rather than being pinned to
///         the lifetime of the context being audited.
///     </para>
/// </remarks>
internal sealed class ExternalContextAuditSink<TKey>(
    AuditOptions options,
    IDbContextOptions contextOptions) : IAuditSink
{
    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyList<AuditEntry> entries,
        AuditWriteContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(context);

        var services = contextOptions
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

        var auditContext = services?.GetService(options.ExternalContextType!) as DbContext
            ?? throw new AuditNotSupportedException(
                $"WriteToContext<{options.ExternalContextType!.Name}>() needs that context from the "
                + "application's service provider, where it is not registered. Register it with "
                + "AddDbContext.");

        var typed = new List<AuditEntry<TKey>>(entries.Count);
        foreach (var entry in entries)
        {
            typed.Add((AuditEntry<TKey>)entry);
        }

        auditContext.Set<AuditEntry<TKey>>().AddRange(typed);

        try
        {
            await auditContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var entry in typed)
            {
                auditContext.Entry(entry).State = EntityState.Detached;
            }
        }

        AuditDiagnostics.ReportEntriesWritten(context.Source, typed.Count, batched: false);
    }
}
