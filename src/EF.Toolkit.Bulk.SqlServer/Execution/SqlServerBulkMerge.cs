using EFToolkit.Bulk.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.SqlServer.Execution;

/// <summary>
///     Upserts on SQL Server using <c>MERGE</c>.
/// </summary>
/// <remarks>
///     The rows are staged with <c>SqlBulkCopy</c> and merged in one statement. <c>OUTPUT</c>
///     carries <c>$action</c> — which says exactly whether each row was inserted or updated — and a
///     source ordinal, so generated values map back to the row that produced them without depending
///     on any ordering the engine never promised.
/// </remarks>
internal sealed class SqlServerBulkMerge
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly Func<SqlBulkCopy> _createBulkCopy;

    private readonly BulkExecutionSettings _settings;

    public SqlServerBulkMerge(
        ISqlGenerationHelper sqlHelper,
        BulkExecutionSettings settings,
        Func<SqlBulkCopy> createBulkCopy)
    {
        _sqlHelper = sqlHelper;
        _settings = settings;
        _createBulkCopy = createBulkCopy;
    }

    public async Task<(int Inserted, int Updated, int Deleted)> ExecuteAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
        bool deleteMissing,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staging = _sqlHelper.DelimitIdentifier($"#efbulk_{Guid.NewGuid():N}");
        var ordinal = _sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName);

        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        await ExecuteNonQueryAsync(
                connection, transaction,
                $"SELECT TOP 0 {columnList}, CAST(0 AS int) AS {ordinal} INTO {staging} FROM {target};",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var bulkCopy = _createBulkCopy())
            {
                bulkCopy.DestinationTableName = staging;
                for (var i = 0; i < writeIndices.Count; i++)
                {
                    bulkCopy.ColumnMappings.Add(i, rows.Columns[writeIndices[i]].Name);
                }

                bulkCopy.ColumnMappings.Add(
                    writeIndices.Count, StagingColumn.OrdinalColumnName);

                await bulkCopy
                    .WriteToServerAsync(
                        new BulkRowSetDataReader(
                            rows, StagingColumn.ForWrite(rows, writeIndices), includeOrdinal: true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (rows.BeforeImages is { } beforeImages)
            {
                await CaptureBeforeAsync(
                        rows, connection, transaction, staging, target, matchIndices, beforeImages,
                        deleteMissing, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await MergeAsync(
                    rows, connection, transaction, staging, target, columnList, writeIndices,
                    matchIndices, readIndices, deleteMissing, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await StagingCleanup.RunAsync(
                    staging,
                    () => ExecuteNonQueryAsync(
                        connection, transaction, $"DROP TABLE IF EXISTS {staging};",
                        CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads the rows a merge or synchronise is about to change or remove, as they stand now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two reads. The first joins the staging table to the target, so every source row
    ///         learns whether the target already held it — the before-image, and with it the per-row
    ///         insert-versus-update split that <c>$action</c> only reports afterwards.
    ///     </para>
    ///     <para>
    ///         The second runs only for a synchronise, and finds the rows its <c>NOT MATCHED BY
    ///         SOURCE</c> arm is about to remove. Those correspond to nothing the caller passed in,
    ///         so they are the one set an observer could not otherwise learn about at all.
    ///     </para>
    /// </remarks>
    private async Task CaptureBeforeAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        string staging,
        string target,
        IReadOnlyList<int> matchIndices,
        BulkBeforeImages beforeImages,
        bool deleteMissing,
        CancellationToken cancellationToken)
    {
        var ordinal = _sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName);

        var projection = string.Join(
            ", ",
            beforeImages.Columns.Select(c => $"t.{_sqlHelper.DelimitIdentifier(c.Name)}"));

        var match = string.Join(
            " AND ",
            matchIndices.Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            }));

        await using (var matched = connection.CreateCommand())
        {
            _settings.Apply(matched);
            matched.Transaction = transaction;

            matched.CommandText =
                $"SELECT s.{ordinal}, {projection} FROM {staging} AS s "
                + $"INNER JOIN {target} AS t WITH (UPDLOCK, HOLDLOCK) ON {match};";

            await using var reader = await matched.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = reader.GetInt32(0);

                for (var i = 0; i < beforeImages.Columns.Count; i++)
                {
                    var position = 1 + i;

                    beforeImages.SetValue(
                        row,
                        i,
                        await reader.IsDBNullAsync(position, cancellationToken).ConfigureAwait(false)
                            ? null
                            : reader.GetValue(position));
                }
            }
        }

        if (!deleteMissing)
        {
            return;
        }

        await using var removed = connection.CreateCommand();
        _settings.Apply(removed);
        removed.Transaction = transaction;

        // The same predicate the delete arm uses, so the rows captured here are exactly the rows it
        // goes on to remove — scope included, since a scope is what holds it back.
        var fence = rows.Scope is { } scope ? $" AND ({scope.Sql})" : "";

        removed.CommandText =
            $"SELECT {projection} FROM {target} AS t WITH (UPDLOCK, HOLDLOCK) WHERE NOT EXISTS "
            + $"(SELECT 1 FROM {staging} AS s WHERE {match}){fence};";

        if (rows.Scope is { } bound)
        {
            // Bound rather than formatted into the SQL, exactly as the delete arm binds them.
            for (var i = 0; i < bound.Parameters.Count; i++)
            {
                removed.Parameters.AddWithValue(
                    "@" + BulkScope.ParameterName(i), bound.Parameters[i] ?? DBNull.Value);
            }
        }

        await using var removedReader = await removed.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await removedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[beforeImages.Columns.Count];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await removedReader.IsDBNullAsync(i, cancellationToken)
                    .ConfigureAwait(false)
                    ? null
                    : removedReader.GetValue(i);
            }

            beforeImages.AddRemovedRow(values);
        }
    }

    private async Task<(int Inserted, int Updated, int Deleted)> MergeAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        string staging,
        string target,
        string columnList,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
        bool deleteMissing,
        CancellationToken cancellationToken)
    {
        var on = string.Join(
            " AND ",
            matchIndices.Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            }));

        // Match columns identify the row, so they are never themselves reassigned, and an
        // insert-only column is written by the insert arm alone.
        var assignments = writeIndices
            .Where(i => !matchIndices.Contains(i) && !rows.Columns[i].IsInsertOnly)
            .Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            })
            .ToList();

        var matchedClause = assignments.Count == 0
            ? ""
            : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", assignments)} ";

        var values = string.Join(
            ", ",
            writeIndices.Select(i => $"s.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        // MERGE has a delete arm for exactly this, so a synchronise stays one statement. NOT
        // MATCHED BY SOURCE sees only the target, which is what the scope selects, so the fence
        // goes straight onto the arm's own condition.
        var fence = rows.Scope is { } scope ? $" AND ({scope.Sql})" : "";

        var notMatchedBySource = deleteMissing
            ? $" WHEN NOT MATCHED BY SOURCE{fence} THEN DELETE"
            : "";

        var output = new List<string> { "$action" };
        output.AddRange(
            readIndices.Select(i => $"inserted.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));
        output.Add($"s.{_sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName)}");

        // HOLDLOCK is not optional on a real merge. Without a serializable range lock over the
        // matched keys, MERGE's match test and its insert are separated by a window another
        // session can insert into, and the upsert fails with a duplicate-key violation instead of
        // updating. The lock is scoped to the keys the join touches, so this is not a table lock.
        var sql = $"""
            MERGE INTO {target} WITH (HOLDLOCK) AS t
            USING {staging} AS s
            ON {on}
            {matchedClause}WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({values}){notMatchedBySource}
            OUTPUT {string.Join(", ", output)};
            """;

        var inserted = 0;
        var updated = 0;
        var deleted = 0;

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        command.Transaction = transaction;

        if (deleteMissing && rows.Scope is { } bound)
        {
            // Bound rather than formatted into the SQL, which is what makes the
            // interpolated-string overload of WithinScope safe to hand a value from outside the
            // application.
            for (var i = 0; i < bound.Parameters.Count; i++)
            {
                command.Parameters.AddWithValue(
                    "@" + BulkScope.ParameterName(i), bound.Parameters[i] ?? DBNull.Value);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var action = reader.GetString(0);

            if (string.Equals(action, "DELETE", StringComparison.Ordinal))
            {
                // A deleted row came from the target, not the source, so both inserted.* and the
                // source ordinal are null and there is nothing to propagate.
                deleted++;
                continue;
            }

            if (string.Equals(action, "INSERT", StringComparison.Ordinal))
            {
                inserted++;
            }
            else
            {
                updated++;
            }

            var row = reader.GetInt32(reader.FieldCount - 1);

            for (var i = 0; i < readIndices.Count; i++)
            {
                var column = i + 1;   // $action occupies position zero
                var value = await reader.IsDBNullAsync(column, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(column);

                rows.SetGeneratedValue(row, readIndices[i], value);
            }
        }

        return (inserted, updated, deleted);
    }

    private async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
