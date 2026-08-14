using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Api;

/// <summary>
///     An optional faster path for writing many audit entries at once.
/// </summary>
/// <remarks>
///     <para>
///         Optional in the literal sense: nothing in this package implements it, and when nothing
///         is registered the same-context sink writes entries through <c>AddRange</c> and
///         <c>SaveChanges</c>, which is correct and entirely adequate for the handful of entries an
///         ordinary save produces.
///     </para>
///     <para>
///         It exists because auditing a hundred-thousand-row bulk operation produces a
///         hundred thousand audit entries, and inserting those one statement at a time would cost
///         more than the operation being audited. Installing EF.Toolkit.Audit.Bulk registers an
///         implementation over <c>BulkInsertAsync</c>; the two packages need no reference to each
///         other for it.
///     </para>
/// </remarks>
public interface IAuditBatchWriter
{
    /// <summary>Writes the entries through <paramref name="context" />.</summary>
    /// <typeparam name="TEntry">The mapped audit entry type.</typeparam>
    /// <param name="context">The context to write through. Any open transaction is joined.</param>
    /// <param name="entries">The entries to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task WriteAsync<TEntry>(
        DbContext context,
        IReadOnlyList<TEntry> entries,
        CancellationToken cancellationToken)
        where TEntry : AuditEntry;
}
