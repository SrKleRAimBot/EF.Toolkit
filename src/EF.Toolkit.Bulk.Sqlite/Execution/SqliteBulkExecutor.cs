using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Sqlite.Execution;

/// <summary>
///     Executes bulk row sets on SQLite through prepared statements reused for every row.
/// </summary>
/// <remarks>
///     <para>
///         SQLite has no bulk-load protocol because it has no need of one: the engine runs in the
///         application's process, and a statement costs a function call rather than a network round
///         trip. What makes a large <c>SaveChanges</c> slow on SQLite is EF Core's per-row work
///         around each statement — see <see cref="SqliteRowStatement" /> — so that is what this
///         removes, and every operation runs as one compiled statement bound once per row inside the
///         surrounding transaction.
///     </para>
///     <para>
///         Executing per row is also what makes this executor the most capable of the four.
///         <c>RETURNING</c> answers for exactly the row just written, so generated values of every
///         kind — keys, defaults, computed columns — correlate without staging or ordinals, and
///         every affected-row count is per row, so a concurrency conflict names its entity exactly.
///         <c>RETURNING</c> arrived in SQLite 3.35; on anything older, only the shapes that read
///         nothing back are accelerated.
///     </para>
/// </remarks>
public sealed class SqliteBulkExecutor : IBulkOperationExecutor
{
    /// <summary>The first SQLite release with <c>RETURNING</c>.</summary>
    private static readonly Version Returning = new(3, 35, 0);

    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly BulkOptions _options;

    /// <summary>Initializes a new executor.</summary>
    /// <param name="sqlHelper">Used to delimit table and column identifiers.</param>
    /// <param name="options">The context's EF.Toolkit.Bulk settings.</param>
    public SqliteBulkExecutor(ISqlGenerationHelper sqlHelper, BulkOptions options)
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
                reason = "a merge needs match columns to locate existing rows by.";
                return false;
            }

            reason = null;
            return true;
        }

        if (rows.EntityState is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            reason = $"{rows.EntityState} has no bulk equivalent.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <inheritdoc />
    public BulkExecutionResult Execute(IBulkRowSet rows, IRelationalConnection connection)
        // Microsoft.Data.Sqlite completes its asynchronous methods synchronously — the engine is
        // in-process, so there is nothing to wait for — which makes this a plain call, not a block.
        => ExecuteAsync(rows, connection).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<BulkExecutionResult> ExecuteAsync(
        IBulkRowSet rows,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);

        // Decided before anything is written, so declining leaves the database untouched.
        if (rows.Columns.Any(c => c.IsRead) && !SupportsReturning(connection.DbConnection.ServerVersion))
        {
            return BulkExecutionResult.Declined(
                $"SQLite {connection.DbConnection.ServerVersion} has no RETURNING clause, which "
                + $"arrived in {Returning}, so the values generated for '{rows.TableName}' cannot "
                + "be read back.");
        }

        var session = new SqliteSession(
            _sqlHelper,
            connection.DbConnection,
            connection.CurrentTransaction?.GetDbTransaction(),
            BulkExecutionSettings.Resolve(rows, _options, connection));

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            var (inserted, updated, deleted) = await new SqliteBulkMerge(session, _options.StagingIndexThreshold)
                .ExecuteAsync(rows, rows.Operation == BulkOperationKind.Synchronize, cancellationToken)
                .ConfigureAwait(false);

            return BulkExecutionResult.Merged(inserted, updated, deleted);
        }

        if (rows.EntityState is EntityState.Modified or EntityState.Deleted)
        {
            return await ApplySetBasedAsync(rows, session, cancellationToken).ConfigureAwait(false);
        }

        return BulkExecutionResult.Executed(
            await InsertAsync(rows, session, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Whether <paramref name="serverVersion" /> names a SQLite with <c>RETURNING</c>.</summary>
    /// <remarks>
    ///     An unparseable version is taken to be capable: every SQLite a supported EF Core ships
    ///     with is, and a statement the engine cannot run fails loudly rather than writing wrongly.
    /// </remarks>
    internal static bool SupportsReturning(string? serverVersion)
        => !Version.TryParse(serverVersion, out var version) || version >= Returning;

    private static async Task<int> InsertAsync(
        IBulkRowSet rows,
        SqliteSession session,
        CancellationToken cancellationToken)
    {
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

        var target = session.Target(rows);
        var layout = StagingColumn.ForWrite(rows, writeIndices);

        var sql = writeIndices.Count == 0
            ? $"INSERT INTO {target} DEFAULT VALUES"
            : $"INSERT INTO {target} ({session.ColumnList(rows, writeIndices)}) VALUES "
                + $"({SqliteSession.Parameters(layout.Count)})";

        if (readIndices.Count == 0)
        {
            await using var plain = session.Statement(sql, rows, layout);

            for (var row = 0; row < rows.RowCount; row++)
            {
                await plain.ExecuteNonQueryAsync(row, cancellationToken).ConfigureAwait(false);
            }

            return rows.RowCount;
        }

        var reads = readIndices.Select(i => rows.Columns[i]).ToList();
        var generated = new BulkGeneratedValues();

        await using (var returning = session.Statement(
            $"{sql} RETURNING {session.ColumnList(rows, readIndices)}", rows, layout))
        {
            for (var row = 0; row < rows.RowCount; row++)
            {
                await using var reader = await returning.ExecuteReaderAsync(row, cancellationToken)
                    .ConfigureAwait(false);

                var current = row;
                await BulkCommandSession
                    .ReadAllAsync(
                        reader, reads, 0,
                        (i, value) => generated.Add(current, readIndices[i], value),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Only once every row is in does anything learn its key — stock EF leaves a failed save's
        // temporary values in place, and assigning as each row landed would leave real ones.
        generated.ApplyTo(rows);
        return rows.RowCount;
    }

    private static async Task<BulkExecutionResult> ApplySetBasedAsync(
        IBulkRowSet rows,
        SqliteSession session,
        CancellationToken cancellationToken)
    {
        var writeIndices = new List<int>();
        var conditionIndices = new List<int>();
        var readIndices = new List<int>();

        for (var i = 0; i < rows.Columns.Count; i++)
        {
            var column = rows.Columns[i];

            if (column.IsCondition)
            {
                conditionIndices.Add(i);
            }

            // A column can be both written and a condition -- a client-managed concurrency token
            // is the usual case -- so this is not an else.
            if (column.IsWrite)
            {
                writeIndices.Add(i);
            }

            if (column.IsRead)
            {
                readIndices.Add(i);
            }
        }

        if (conditionIndices.Count == 0)
        {
            return BulkExecutionResult.Declined(
                $"'{rows.TableName}' has no columns to locate a {rows.EntityState} row by.");
        }

        if (rows.EntityState == EntityState.Modified && writeIndices.Count == 0)
        {
            return BulkExecutionResult.Declined("the update has no columns to set.");
        }

        var operations = new SqliteBulkWriteOperations(session);

        var affected = rows.EntityState == EntityState.Deleted
            ? await operations.DeleteAsync(rows, conditionIndices, cancellationToken)
                .ConfigureAwait(false)
            : await operations
                .UpdateAsync(rows, writeIndices, conditionIndices, readIndices, cancellationToken)
                .ConfigureAwait(false);

        return BulkExecutionResult.Executed(affected);
    }
}
