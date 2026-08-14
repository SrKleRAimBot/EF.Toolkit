using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Opens the transaction that keeps an audit entry and the change it describes together.
/// </summary>
/// <remarks>
///     <para>
///         Modelled on EF.Toolkit.Bulk's <c>BulkTransaction</c>, and it distinguishes the same three
///         cases: an EF transaction already open, an ambient
///         <see cref="System.Transactions.TransactionScope" /> — which does not show up as one, but
///         the connection enlists in it when it opens — and neither. In the first two the caller
///         owns the unit of work and decides when it commits, so this must not.
///     </para>
///     <para>
///         It does not share that implementation because the shape is different: a bulk operation
///         hands over a body to run, while auditing has to open the transaction before EF saves and
///         commit it after the entries are written, across two separate interception points. The
///         packages are also independent by design, and thirty lines of well-understood transaction
///         handling is a cheaper price than a reference between them.
///     </para>
/// </remarks>
internal static class AuditTransaction
{
    /// <summary>Opens a transaction if auditing needs to own one, otherwise returns null.</summary>
    /// <param name="context">The context being saved.</param>
    /// <param name="options">The context's auditing settings.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async ValueTask<IDbContextTransaction?> BeginAsync(
        DbContext context,
        AuditOptions options,
        CancellationToken cancellationToken)
        => ShouldOwn(context, options)
            ? await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>Opens a transaction if auditing needs to own one, otherwise returns null.</summary>
    /// <param name="context">The context being saved.</param>
    /// <param name="options">The context's auditing settings.</param>
    public static IDbContextTransaction? Begin(DbContext context, AuditOptions options)
        => ShouldOwn(context, options) ? context.Database.BeginTransaction() : null;

    private static bool ShouldOwn(DbContext context, AuditOptions options)
    {
        if (options.Atomicity != AuditAtomicity.SameTransaction
            || context.Database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null)
        {
            return false;
        }

        if (context.Database.CreateExecutionStrategy().RetriesOnFailure)
        {
            // A retrying strategy refuses a user-initiated transaction unless the whole unit of
            // work — begin, save, commit — is inside its own retry loop, and an interceptor is not.
            // EF's own transaction is opened after SavingChanges and committed before SavedChanges,
            // so there is nothing to join either. Saying so beats opening a second transaction that
            // commits separately while the configuration claims otherwise.
            throw new AuditNotSupportedException(
                "Auditing is configured with Atomicity(AuditAtomicity.SameTransaction) and this "
                + "context uses a retrying execution strategy, so it cannot open the transaction "
                + "that would keep an audit entry and its change together. Either wrap the save in "
                + "Database.CreateExecutionStrategy().ExecuteAsync(...) with your own transaction — "
                + "which auditing then joins — or configure "
                + "Atomicity(AuditAtomicity.BestEffort).");
        }

        return true;
    }
}
