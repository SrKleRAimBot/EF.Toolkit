using EFBulk.Configuration;
using EFBulk.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFBulk.SqlServer.Execution;

/// <summary>
///     Executes bulk row sets on SQL Server using <c>SqlBulkCopy</c>.
/// </summary>
/// <remarks>
///     <para>
///         <c>SqlBulkCopy</c> uses the TDS bulk-load protocol, which skips per-row statement
///         processing entirely. Like PostgreSQL's <c>COPY</c> it returns nothing, so it is used
///         directly only when the insert has nothing to read back.
///     </para>
///     <para>
///         Unlike PostgreSQL there is no sequence behind an <c>IDENTITY</c> column to reserve from,
///         so rows needing generated values back take the staging path — see
///         <see cref="SqlServerStagingInsert" />.
///     </para>
/// </remarks>
public sealed class SqlServerBulkExecutor : IBulkOperationExecutor
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly BulkOptions _options;

    /// <summary>Initializes a new executor.</summary>
    /// <param name="sqlHelper">Used to delimit table and column identifiers.</param>
    /// <param name="options">The context's EF.Bulk settings.</param>
    public SqlServerBulkExecutor(ISqlGenerationHelper sqlHelper, BulkOptions options)
    {
        _sqlHelper = sqlHelper;
        _options = options;
    }

    /// <inheritdoc />
    public bool CanExecute(IBulkRowSet rows, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            if (!rows.Columns.Any(c => c.IsCondition))
            {
                reason = "a merge needs match columns to join on.";
                return false;
            }

            reason = null;
            return true;
        }

        if (rows.EntityState is EntityState.Modified or EntityState.Deleted)
        {
            foreach (var column in rows.Columns)
            {
                if (column.IsRead)
                {
                    reason = $"'{column.Name}' is store-generated and must be read back after the "
                        + "write, which the set-based path does not yet support.";
                    return false;
                }

                if (column.IsWrite && column.IsCondition)
                {
                    // Such a column needs its loaded value in the WHERE clause and its new value in
                    // the SET clause at once — two values for one column, which the row set exposes
                    // only one of. A rowversion updated in place is the case that hits this.
                    reason = $"'{column.Name}' is both written and used to locate the row, so it "
                        + "needs its original and current values simultaneously.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        if (rows.EntityState != EntityState.Added)
        {
            reason = $"{rows.EntityState} has no bulk equivalent.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <inheritdoc />
    public BulkExecutionResult Execute(IBulkRowSet rows, IRelationalConnection connection)
        // SqlBulkCopy's synchronous and asynchronous paths do the same protocol work, and EF only
        // takes this route when the caller used the synchronous SaveChanges.
        => ExecuteAsync(rows, connection).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<BulkExecutionResult> ExecuteAsync(
        IBulkRowSet rows,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);

        var sqlConnection = connection.DbConnection as SqlConnection
            ?? throw new BulkNotSupportedException(
                "EF.Bulk.SqlServer requires a SqlConnection, but the context is using "
                + $"'{connection.DbConnection.GetType().Name}'.");

        // Enlisting in EF's ambient transaction is not optional: without it SqlBulkCopy opens its
        // own implicit transaction, so copied rows would commit even when the surrounding save
        // rolls back.
        var transaction = connection.CurrentTransaction?.GetDbTransaction() as SqlTransaction;

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            return await MergeAsync(rows, sqlConnection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        if (rows.EntityState is EntityState.Modified or EntityState.Deleted)
        {
            return await ApplySetBasedAsync(rows, sqlConnection, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        var writeIndices = new List<int>();
        var readIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            if (rows.Columns[i].IsRead)
            {
                readIndices.Add(i);
            }
            else if (rows.Columns[i].IsWrite)
            {
                writeIndices.Add(i);
            }
        }

        if (readIndices.Count == 0)
        {
            using var bulkCopy = CreateBulkCopy(sqlConnection, transaction);
            bulkCopy.DestinationTableName =
                _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

            for (var i = 0; i < writeIndices.Count; i++)
            {
                bulkCopy.ColumnMappings.Add(i, rows.Columns[writeIndices[i]].Name);
            }

            await bulkCopy
                .WriteToServerAsync(
                    new BulkRowSetDataReader(rows, writeIndices, includeOrdinal: false),
                    cancellationToken)
                .ConfigureAwait(false);

            return BulkExecutionResult.Executed(rows.RowCount);
        }

        var affected = await new SqlServerStagingInsert(_sqlHelper)
            .ExecuteAsync(
                rows, sqlConnection, transaction, writeIndices, readIndices,
                () => CreateBulkCopy(sqlConnection, transaction), cancellationToken)
            .ConfigureAwait(false);

        return BulkExecutionResult.Executed(affected);
    }

    private async Task<BulkExecutionResult> MergeAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var writeIndices = new List<int>();
        var matchIndices = new List<int>();
        var readIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];

            if (column.IsWrite)
            {
                writeIndices.Add(i);
            }

            if (column.IsCondition)
            {
                matchIndices.Add(i);
            }

            if (column.IsRead)
            {
                readIndices.Add(i);
            }
        }

        var (inserted, updated, deleted) = await new SqlServerBulkMerge(
                _sqlHelper,
                () => CreateBulkCopy(connection, transaction, SqlBulkCopyOptions.KeepIdentity))
            .ExecuteAsync(
                rows, connection, transaction, writeIndices, matchIndices, readIndices,
                rows.Operation == BulkOperationKind.Synchronize, cancellationToken)
            .ConfigureAwait(false);

        return BulkExecutionResult.Merged(inserted, updated, deleted);
    }

    /// <summary>
    ///     Applies an update or delete as a single set-based statement joined to a staged copy of
    ///     the rows.
    /// </summary>
    private async Task<BulkExecutionResult> ApplySetBasedAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var writeIndices = new List<int>();
        var conditionIndices = new List<int>();
        var keyIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];

            if (column.IsKey)
            {
                keyIndices.Add(i);
            }

            if (column.IsCondition)
            {
                conditionIndices.Add(i);
            }
            else if (column.IsWrite)
            {
                writeIndices.Add(i);
            }
        }

        if (keyIndices.Count == 0 || conditionIndices.Count == 0)
        {
            return BulkExecutionResult.Declined(
                $"'{rows.TableName}' has no key columns to join a staged {rows.EntityState} on.");
        }

        if (rows.EntityState == EntityState.Modified && writeIndices.Count == 0)
        {
            return BulkExecutionResult.Declined("the update has no columns to set.");
        }

        var operations = new SqlServerBulkWriteOperations(
            _sqlHelper,
            () => CreateBulkCopy(connection, transaction, SqlBulkCopyOptions.KeepIdentity));

        var affected = rows.EntityState == EntityState.Deleted
            ? await operations
                .DeleteAsync(
                    rows, connection, transaction, conditionIndices, keyIndices, cancellationToken)
                .ConfigureAwait(false)
            : await operations
                .UpdateAsync(
                    rows, connection, transaction, writeIndices, conditionIndices, keyIndices,
                    cancellationToken)
                .ConfigureAwait(false);

        return BulkExecutionResult.Executed(affected);
    }

    /// <summary>Creates a bulk copy enlisted in the current transaction.</summary>
    /// <param name="connection">The connection to write through.</param>
    /// <param name="transaction">EF's ambient transaction, if any.</param>
    /// <param name="options">
    ///     Pass <see cref="SqlBulkCopyOptions.KeepIdentity" /> when copying into a staging table
    ///     whose columns were derived with <c>SELECT ... INTO</c>. That syntax carries the IDENTITY
    ///     property across, so without it the server would silently assign fresh values in place of
    ///     the keys being written, and every subsequent join would match nothing.
    /// </param>
    private SqlBulkCopy CreateBulkCopy(
        SqlConnection connection,
        SqlTransaction? transaction,
        SqlBulkCopyOptions options = SqlBulkCopyOptions.Default)
    {
        var bulkCopy = new SqlBulkCopy(connection, options, transaction)
        {
            BatchSize = _options.MaxBatchSize,
            EnableStreaming = true
        };

        if (_options.Timeout is { } timeout)
        {
            bulkCopy.BulkCopyTimeout = (int)timeout.TotalSeconds;
        }

        return bulkCopy;
    }
}
