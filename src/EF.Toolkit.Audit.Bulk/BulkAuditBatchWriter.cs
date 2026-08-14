using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Bulk;

/// <summary>
///     Writes audit entries through EF.Toolkit.Bulk.
/// </summary>
/// <remarks>
///     Auditing a hundred-thousand-row bulk operation produces a hundred thousand audit entries.
///     Inserting those one statement at a time would cost more than the operation being audited, and
///     turn "we turned auditing on" into "the import got slower by an order of magnitude". This
///     writes them the same way the rows themselves were written.
/// </remarks>
internal sealed class BulkAuditBatchWriter : IAuditBatchWriter
{
    /// <inheritdoc />
    public async Task WriteAsync<TEntry>(
        DbContext context,
        IReadOnlyList<TEntry> entries,
        CancellationToken cancellationToken)
        where TEntry : AuditEntry
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);

        await context
            .BulkInsertAsync(
                entries,
                o => o
                    // Already inside the operation's transaction, and an audit write that fails
                    // must take the change with it rather than rolling back only itself.
                    .WithoutSavepoint()

                    // Audit entries are not audited. Without this the observer would see this write
                    // and try to record it, and the recording of that, and so on.
                    .WithoutObservers(),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
