using Microsoft.EntityFrameworkCore.Storage;

namespace EFBulk.Execution;

/// <summary>
///     Executes a <see cref="IBulkRowSet" /> using a database engine's native bulk-loading
///     facility — <c>COPY</c> on PostgreSQL, <c>SqlBulkCopy</c> on SQL Server.
/// </summary>
/// <remarks>
///     Implemented once per provider package. An implementation is responsible for propagating
///     store-generated values back onto the row set's commands, so that EF's change tracker ends
///     in the state it would have reached through the normal update pipeline.
/// </remarks>
public interface IBulkOperationExecutor
{
    /// <summary>
    ///     Whether this executor can handle <paramref name="rows" /> on the current database.
    /// </summary>
    /// <param name="rows">The rows being considered.</param>
    /// <param name="reason">
    ///     When the result is <see langword="false" />, a human-readable explanation suitable for a
    ///     diagnostic message or <see cref="BulkNotSupportedException" />.
    /// </param>
    /// <returns><see langword="true" /> if <see cref="Execute" /> can be called.</returns>
    bool CanExecute(IBulkRowSet rows, out string? reason);

    /// <summary>Executes <paramref name="rows" /> and propagates any store-generated values.</summary>
    /// <param name="rows">The rows to write.</param>
    /// <param name="connection">An open connection.</param>
    /// <returns>
    ///     <see cref="BulkExecutionResult.Executed" />, or
    ///     <see cref="BulkExecutionResult.Declined" /> if a check that needed the database ruled the
    ///     partition out — in which case nothing may have been written.
    /// </returns>
    BulkExecutionResult Execute(IBulkRowSet rows, IRelationalConnection connection);

    /// <summary>Executes <paramref name="rows" /> and propagates any store-generated values.</summary>
    /// <param name="rows">The rows to write.</param>
    /// <param name="connection">An open connection.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    ///     <see cref="BulkExecutionResult.Executed" />, or
    ///     <see cref="BulkExecutionResult.Declined" /> if a check that needed the database ruled the
    ///     partition out — in which case nothing may have been written.
    /// </returns>
    Task<BulkExecutionResult> ExecuteAsync(
        IBulkRowSet rows,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default);
}
