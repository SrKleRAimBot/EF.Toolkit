using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Audit.Api;

/// <summary>
///     Where audit entries go.
/// </summary>
/// <remarks>
///     The built-in sinks write through the context that produced the change — in its transaction,
///     so entry and change commit together — or through a dedicated audit context. Implement this
///     for anything else: an outbox table, a message bus, an append-only log.
/// </remarks>
public interface IAuditSink
{
    /// <summary>Writes the entries.</summary>
    /// <param name="entries">The entries to write. Never empty.</param>
    /// <param name="context">Where the change that produced them was made.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <remarks>
    ///     Called inside the change's transaction where one exists, so a sink that writes elsewhere
    ///     must expect its work to be discarded if that transaction rolls back — which is why
    ///     anything but the same-database sinks needs
    ///     <c>Atomicity(AuditAtomicity.BestEffort)</c>.
    /// </remarks>
    Task WriteAsync(
        IReadOnlyList<AuditEntry> entries,
        AuditWriteContext context,
        CancellationToken cancellationToken);
}

/// <summary>
///     What a sink is told about the change its entries describe.
/// </summary>
/// <param name="Context">The context the change was made through.</param>
/// <param name="Transaction">
///     The transaction the change was made in, or <see langword="null" /> when it was made outside
///     one. A sink writing to the same database should join it rather than open its own.
/// </param>
/// <param name="Source">
///     Which write path produced the change — <c>SaveChanges</c>, <c>Bulk.Insert</c>.
/// </param>
public sealed record AuditWriteContext(
    DbContext Context,
    IDbContextTransaction? Transaction,
    string Source);
