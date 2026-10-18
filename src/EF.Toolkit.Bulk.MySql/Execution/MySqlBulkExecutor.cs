using System.Globalization;
using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.MySql.Execution;

/// <summary>
///     Executes bulk row sets on MySQL using multi-row <c>INSERT</c> and staging-table joins.
/// </summary>
/// <remarks>
///     <para>
///         MySQL has neither <c>RETURNING</c> nor <c>OUTPUT</c>, so nothing a statement generates
///         comes back with it. What it has instead is <c>LAST_INSERT_ID()</c>, which reports the
///         <c>AUTO_INCREMENT</c> value given to the first row of the last insert — and a documented
///         guarantee, under InnoDB's "traditional" and "consecutive" lock modes, that every row of
///         a multi-row <c>INSERT ... VALUES</c> gets the next value in sequence. Together those
///         recover every row's key exactly, one statement per thousand rows.
///     </para>
///     <para>
///         The guarantee is the whole of it, so it is checked rather than assumed. Under the
///         "interleaved" lock mode — MySQL 8's default — values generated for one statement may
///         interleave with a concurrent statement's, and a run of keys cannot be inferred from the
///         first. There each row is inserted by a statement of its own, followed by the
///         <c>LAST_INSERT_ID()</c> that names its key: a chunk of them still travels in one round
///         trip, but the server parses every row, so <c>innodb_autoinc_lock_mode = 1</c> is worth
///         setting where inserts are hot. Rows that bring their own keys, and every update, delete
///         and merge, are unaffected.
///     </para>
///     <para>
///         Anything else an insert generates — a default, a computed column — has no equivalent of
///         <c>LAST_INSERT_ID()</c>, so an insert that would need one read back declines to stock EF
///         Core. A merge does not: it finds every row it wrote again through its match columns, and
///         reads whatever it needs from there.
///     </para>
/// </remarks>
public sealed class MySqlBulkExecutor : IBulkOperationExecutor
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly BulkOptions _options;
    private readonly MySqlAutoIncrementResolver _autoIncrement;

    /// <summary>Initializes a new executor.</summary>
    /// <param name="sqlHelper">Used to delimit table and column identifiers.</param>
    /// <param name="options">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="autoIncrement">Establishes which key columns are <c>AUTO_INCREMENT</c>.</param>
    public MySqlBulkExecutor(
        ISqlGenerationHelper sqlHelper,
        BulkOptions options,
        MySqlAutoIncrementResolver autoIncrement)
    {
        _sqlHelper = sqlHelper;
        _options = options;
        _autoIncrement = autoIncrement;
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

            // Every generated value is read back by joining the staging table to the target on the
            // match columns, which identify inserted rows as well as updated ones.
            reason = null;
            return true;
        }

        switch (rows.EntityState)
        {
            case EntityState.Added:
                return CanReadBackInserted(rows, out reason);

            case EntityState.Modified:
            case EntityState.Deleted:
                // Concurrency tokens are handled rather than declined: the loaded value is staged
                // to join on, the new value is staged separately to assign, and anything the
                // database regenerated is read back by joining the staging table again.
                reason = null;
                return true;

            default:
                reason = $"{rows.EntityState} has no bulk equivalent.";
                return false;
        }
    }

    /// <inheritdoc />
    public BulkExecutionResult Execute(IBulkRowSet rows, IRelationalConnection connection)
        // The statements are ordinary commands either way, and EF only takes this path when the
        // caller used the synchronous SaveChanges.
        => ExecuteAsync(rows, connection).GetAwaiter().GetResult();

    /// <inheritdoc />
    public async Task<BulkExecutionResult> ExecuteAsync(
        IBulkRowSet rows,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(connection);

        var session = new MySqlSession(
            _sqlHelper,
            connection.DbConnection,
            connection.CurrentTransaction?.GetDbTransaction(),
            BulkExecutionSettings.Resolve(rows, _options, connection));

        if (rows.Operation is BulkOperationKind.Merge or BulkOperationKind.Synchronize)
        {
            var (inserted, updated, deleted) = await new MySqlBulkMerge(
                    session, _options.StagingIndexThreshold)
                .ExecuteAsync(rows, rows.Operation == BulkOperationKind.Synchronize, cancellationToken)
                .ConfigureAwait(false);

            return BulkExecutionResult.Merged(inserted, updated, deleted);
        }

        if (rows.EntityState is EntityState.Modified or EntityState.Deleted)
        {
            return await ApplySetBasedAsync(rows, session, cancellationToken).ConfigureAwait(false);
        }

        // Everything that could rule the row set out happens before the first row is written, so
        // declining leaves the database untouched and the caller can safely fall back.
        var (declined, keys) = await PrepareKeysAsync(rows, session, cancellationToken)
            .ConfigureAwait(false);

        if (declined is not null)
        {
            return BulkExecutionResult.Declined(declined);
        }

        var writeIndices = new List<int>();
        for (var i = 0; i < rows.Columns.Count; i++)
        {
            if (rows.Columns[i].IsWrite && !rows.Columns[i].IsRead)
            {
                writeIndices.Add(i);
            }
        }

        var generated = new BulkGeneratedValues();

        await session
            .InsertRowsAsync(
                session.Target(rows),
                session.ColumnList(rows, writeIndices),
                rows,
                StagingColumn.ForWrite(rows, writeIndices),
                includeOrdinal: false,
                AllRows(rows.RowCount),
                keys,
                generated,
                cancellationToken)
            .ConfigureAwait(false);

        // Only now that every statement has succeeded does anything learn its key. Stock EF leaves
        // temporary key values in place when a save fails, and assigning earlier would leave real
        // ones behind instead.
        generated.ApplyTo(rows);

        return BulkExecutionResult.Executed(rows.RowCount);
    }

    /// <summary>The row positions <c>0 .. count - 1</c>.</summary>
    internal static int[] AllRows(int count)
    {
        var all = new int[count];
        for (var i = 0; i < count; i++)
        {
            all[i] = i;
        }

        return all;
    }

    /// <summary>
    ///     Whether every value an insert of these rows generates can be learned afterwards — which
    ///     on MySQL means at most one, and that one an <c>AUTO_INCREMENT</c> key.
    /// </summary>
    private static bool CanReadBackInserted(IBulkRowSet rows, out string? reason)
    {
        var generatedKeys = 0;

        foreach (var column in rows.Columns)
        {
            if (!column.IsRead)
            {
                continue;
            }

            if (!column.IsKey)
            {
                reason = $"'{column.Name}' is store-generated and is not a key, and MySQL has no "
                    + "RETURNING clause to read it back through.";
                return false;
            }

            generatedKeys++;
        }

        if (generatedKeys > 1)
        {
            reason = "more than one key column is store-generated, and MySQL allows only one "
                + "AUTO_INCREMENT column per table.";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    ///     Confirms that the generated key, if there is one, can be correlated through
    ///     <c>LAST_INSERT_ID()</c>, and learns how: from one statement's first value and the step
    ///     between values, or one statement per row.
    /// </summary>
    private async Task<(string? Declined, GeneratedKeys? Keys)> PrepareKeysAsync(
        IBulkRowSet rows,
        MySqlSession session,
        CancellationToken cancellationToken)
    {
        var index = -1;
        for (var i = 0; i < rows.Columns.Count; i++)
        {
            if (rows.Columns[i].IsRead)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            return (null, null);
        }

        var column = rows.Columns[index];

        var isAutoIncrement = await _autoIncrement
            .IsAutoIncrementAsync(
                session.Connection, session.Transaction, rows.Schema, rows.TableName, column.Name,
                cancellationToken)
            .ConfigureAwait(false);

        if (!isAutoIncrement)
        {
            return ($"'{column.Name}' is generated but is not AUTO_INCREMENT, so "
                + "LAST_INSERT_ID() cannot say what it was given.", null);
        }

        await using var command = session.CreateCommand(
            "SELECT @@innodb_autoinc_lock_mode, @@auto_increment_increment");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        var lockMode = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var increment = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);

        // "Traditional" and "consecutive" guarantee a multi-row INSERT ... VALUES its keys in an
        // unbroken run; "interleaved" does not, and then each row is inserted on its own.
        return (null, new GeneratedKeys(
            index, column.ProviderClrType, Math.Max(1, increment), Consecutive: lockMode is 0 or 1));
    }

    private async Task<BulkExecutionResult> ApplySetBasedAsync(
        IBulkRowSet rows,
        MySqlSession session,
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

        // The condition columns, not the key: with MatchOn the caller locates rows by something
        // else entirely, and a set-based UPDATE or DELETE needs no unique index to do it.
        if (conditionIndices.Count == 0)
        {
            return BulkExecutionResult.Declined(
                $"'{rows.TableName}' has no columns to join a staged {rows.EntityState} on.");
        }

        if (rows.EntityState == EntityState.Modified && writeIndices.Count == 0)
        {
            return BulkExecutionResult.Declined("the update has no columns to set.");
        }

        if (rows.EntityState == EntityState.Modified
            && readIndices.Count > 0
            && conditionIndices.All(readIndices.Contains))
        {
            // Regenerated values are read back by joining the staging table to the target again,
            // which needs at least one locating column whose value the update did not change.
            return BulkExecutionResult.Declined(
                $"every column locating a '{rows.TableName}' row is regenerated by the update, so "
                + "the rows cannot be found again to read the new values back.");
        }

        var operations = new MySqlBulkWriteOperations(session, _options.StagingIndexThreshold);

        var affected = rows.EntityState == EntityState.Deleted
            ? await operations.DeleteAsync(rows, conditionIndices, cancellationToken)
                .ConfigureAwait(false)
            : await operations
                .UpdateAsync(rows, writeIndices, conditionIndices, readIndices, cancellationToken)
                .ConfigureAwait(false);

        return BulkExecutionResult.Executed(affected);
    }
}

/// <summary>How the generated key of an insert is recovered from <c>LAST_INSERT_ID()</c>.</summary>
/// <param name="Index">The key column's index into the row set's columns.</param>
/// <param name="ProviderType">The key's provider type, which the values are narrowed to.</param>
/// <param name="Increment">The session's <c>auto_increment_increment</c>.</param>
/// <param name="Consecutive">
///     Whether the server guarantees one statement's values are consecutive, so a multi-row insert's
///     keys follow from the first. When it does not, each row is inserted by its own statement.
/// </param>
internal sealed record GeneratedKeys(int Index, Type ProviderType, long Increment, bool Consecutive)
{
    /// <summary>Records <paramref name="value" /> as <paramref name="row" />'s key.</summary>
    public void Assign(BulkGeneratedValues generated, int row, long value)
    {
        if (value == 0)
        {
            throw new BulkNotSupportedException(
                "LAST_INSERT_ID() reported no generated value for an insert that should have "
                + "generated one. This indicates a bug in EF.Toolkit.Bulk.MySql's key handling.");
        }

        generated.Add(
            row,
            Index,
            ProviderType == typeof(long)
                ? value
                : Convert.ChangeType(value, ProviderType, CultureInfo.InvariantCulture));
    }
}
