using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Runs a bulk operation as one atomic unit, inside whatever transaction already exists.
/// </summary>
/// <remarks>
///     <para>
///         A bulk operation is several statements — a staging table, a copy, a set-based statement,
///         a drop — and none of them is individually atomic, so all of them have to share a
///         transaction to fail as a unit the way a single <c>SaveChanges</c> statement would.
///     </para>
///     <para>
///         The unit of work may already belong to someone else, in two forms this has to tell
///         apart. An EF transaction shows up as <c>Database.CurrentTransaction</c>. An
///         ambient <see cref="System.Transactions.TransactionScope" /> does not — EF reports no
///         current transaction — but the connection enlists in it when it opens, and beginning a
///         second transaction on top would throw. In both cases the caller decides when to commit,
///         and this must not.
///     </para>
/// </remarks>
internal static class BulkTransaction
{
    /// <summary>
    ///     Runs <paramref name="body" /> inside a transaction, beginning and committing one only if
    ///     nothing else already owns the unit of work.
    /// </summary>
    /// <param name="context">The context the operation belongs to.</param>
    /// <param name="useSavepoint">
    ///     Whether to wrap the body in a savepoint when a caller transaction is already open.
    /// </param>
    /// <param name="body">The work to perform.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public static async Task<T> RunAsync<T>(
        DbContext context,
        bool useSavepoint,
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is { } caller)
        {
            return await WithSavepointAsync(caller, useSavepoint, body, cancellationToken)
                .ConfigureAwait(false);
        }

        if (System.Transactions.Transaction.Current is not null)
        {
            // A TransactionScope owns the unit of work. The connection enlists when it opens, so
            // the writes are already covered; savepoints are not available because there is no EF
            // transaction to hang one on.
            return await body(cancellationToken).ConfigureAwait(false);
        }

        // Retrying strategies refuse a user-initiated transaction unless the whole thing — begin,
        // work, commit — is inside the strategy's own retry loop, so a context configured with
        // EnableRetryOnFailure could not use the explicit API at all before this.
        var strategy = context.Database.CreateExecutionStrategy();

        return await strategy
            .ExecuteAsync(
                async ct =>
                {
                    await using var transaction = await context.Database
                        .BeginTransactionAsync(ct)
                        .ConfigureAwait(false);

                    var result = await body(ct).ConfigureAwait(false);

                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return result;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs <paramref name="body" /> behind a savepoint, so a failure rolls back only this
    ///     operation rather than the caller's whole transaction.
    /// </summary>
    /// <remarks>
    ///     This is what <c>SaveChanges</c> does inside a user transaction, and it matters most on
    ///     PostgreSQL, where a failed statement aborts the entire transaction and every subsequent
    ///     one fails until a rollback. Without a savepoint a caller who catches a bulk failure is
    ///     left holding a transaction that can no longer do anything.
    /// </remarks>
    private static async Task<T> WithSavepointAsync<T>(
        IDbContextTransaction transaction,
        bool useSavepoint,
        Func<CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        if (!useSavepoint || !transaction.SupportsSavepoints)
        {
            return await body(cancellationToken).ConfigureAwait(false);
        }

        var name = $"efbulk_{Guid.NewGuid():N}"[..16];

        await transaction.CreateSavepointAsync(name, cancellationToken).ConfigureAwait(false);

        T result;
        try
        {
            result = await body(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackToSavepointAsync(name, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await transaction.ReleaseSavepointAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Not every engine can release a savepoint — SQL Server has no such statement — and an
            // unreleased one is harmless: it is discarded when the transaction ends. Failing the
            // operation here would turn a successful write into an error.
        }

        return result;
    }
}
