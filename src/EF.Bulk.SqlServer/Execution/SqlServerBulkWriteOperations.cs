using EFBulk.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFBulk.SqlServer.Execution;

/// <summary>
///     Bulk <c>UPDATE</c> and <c>DELETE</c> on SQL Server, via a temporary table joined to the
///     target.
/// </summary>
/// <remarks>
///     The rows are streamed into a temporary table with <c>SqlBulkCopy</c> and then applied in a
///     single set-based statement. Both statements carry an <c>OUTPUT</c> clause so the keys
///     actually matched come back: a bulk statement reports one affected-row count for the whole
///     set, and recovering the detail is what lets a concurrency conflict name the entities
///     involved rather than just the total.
/// </remarks>
internal sealed class SqlServerBulkWriteOperations
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly Func<SqlBulkCopy> _createBulkCopy;

    public SqlServerBulkWriteOperations(ISqlGenerationHelper sqlHelper, Func<SqlBulkCopy> createBulkCopy)
    {
        _sqlHelper = sqlHelper;
        _createBulkCopy = createBulkCopy;
    }

    public Task<int> UpdateAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);

        return WithStagingAsync(
            rows, connection, transaction, staged, keyIndices, readIndices,
            staging =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        var source = _sqlHelper.DelimitIdentifier(StagedName(staged, i, false));
                        return $"t.{column} = s.{source}";
                    }));

                // OUTPUT precedes FROM in SQL Server's UPDATE ... FROM form.
                return $"UPDATE t SET {assignments} "
                    + $"OUTPUT {Output(rows, keyIndices, readIndices, "inserted")} "
                    + $"FROM {target} AS t INNER JOIN {staging} AS s "
                    + $"ON {JoinPredicate(rows, conditionIndices, staged)};";
            },
            cancellationToken);
    }

    public Task<int> DeleteAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForDelete(rows, conditionIndices);

        return WithStagingAsync(
            rows, connection, transaction, staged, keyIndices, [],
            staging =>
                $"DELETE t OUTPUT {Output(rows, keyIndices, [], "deleted")} "
                + $"FROM {target} AS t INNER JOIN {staging} AS s "
                + $"ON {JoinPredicate(rows, conditionIndices, staged)};",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        List<StagingColumn> staged,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices,
        Func<string, string> buildSql,
        CancellationToken cancellationToken)
    {
        var staging = _sqlHelper.DelimitIdentifier($"#efbulk_{Guid.NewGuid():N}");
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        // Aliased so a column staged twice -- a concurrency token's loaded and new values -- gets
        // two distinct staging columns of the correct type.
        var projection = string.Join(
            ", ",
            staged.Select(c =>
                $"{_sqlHelper.DelimitIdentifier(rows.Columns[c.Index].Name)} AS "
                + $"{_sqlHelper.DelimitIdentifier(c.Name)}"));

        await ExecuteNonQueryAsync(
                connection, transaction,
                $"SELECT TOP 0 {projection} INTO {staging} FROM {target};",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var bulkCopy = _createBulkCopy())
            {
                bulkCopy.DestinationTableName = staging;
                for (var i = 0; i < staged.Count; i++)
                {
                    bulkCopy.ColumnMappings.Add(i, staged[i].Name);
                }

                await bulkCopy
                    .WriteToServerAsync(
                        new BulkRowSetDataReader(rows, staged, includeOrdinal: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var matched = new HashSet<string>(StringComparer.Ordinal);
            var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var row = 0; row < rows.RowCount; row++)
            {
                byKey[BulkRowMatching.KeyOf(rows, row, keyIndices)] = row;
            }

            var keyValues = new object?[keyIndices.Count];

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = buildSql(staging);
                command.Transaction = transaction;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    for (var i = 0; i < keyIndices.Count; i++)
                    {
                        keyValues[i] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                            ? null
                            : reader.GetValue(i);
                    }

                    var key = BulkRowMatching.KeyOf(keyValues);
                    matched.Add(key);

                    // Anything the database regenerated -- a concurrency token, a computed column
                    // -- comes back here so the entity ends up matching the row.
                    if (readIndices.Count > 0 && byKey.TryGetValue(key, out var row))
                    {
                        for (var i = 0; i < readIndices.Count; i++)
                        {
                            var ordinal = keyIndices.Count + i;
                            var value = await reader.IsDBNullAsync(ordinal, cancellationToken).ConfigureAwait(false)
                                ? null
                                : reader.GetValue(ordinal);

                            rows.SetGeneratedValue(row, readIndices[i], value);
                        }
                    }
                }
            }

            BulkRowMatching.ThrowIfAnyMissing(rows, keyIndices, matched);
            return matched.Count;
        }
        finally
        {
            // Temp tables die with the session, but connections are pooled and reused, so one left
            // behind would outlive the operation that created it.
            await ExecuteNonQueryAsync(
                    connection, transaction, $"DROP TABLE IF EXISTS {staging};", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private string JoinPredicate(
        IBulkRowSet rows,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<StagingColumn> staged)
        => string.Join(
            " AND ",
            conditionIndices.Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                var source = _sqlHelper.DelimitIdentifier(StagedName(staged, i, true));
                return $"t.{column} = s.{source}";
            }));

    private string Output(
        IBulkRowSet rows,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices,
        string source)
        => string.Join(
            ", ",
            keyIndices.Concat(readIndices)
                .Select(i => $"{source}.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

    private static string StagedName(
        IReadOnlyList<StagingColumn> staged,
        int index,
        bool original)
    {
        foreach (var column in staged)
        {
            if (column.Index == index && column.UseOriginal == original)
            {
                return column.Name;
            }
        }

        throw new BulkNotSupportedException(
            $"Column index {index} was not staged, which indicates a bug in EF.Bulk's staging "
            + "layout.");
    }

    private static async Task ExecuteNonQueryAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
