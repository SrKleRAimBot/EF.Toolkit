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
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = Union(conditionIndices, writeIndices);

        return WithStagingAsync(
            rows, connection, transaction, staged, keyIndices,
            staging =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        return $"t.{column} = s.{column}";
                    }));

                // OUTPUT precedes FROM in SQL Server's UPDATE ... FROM form.
                return $"UPDATE t SET {assignments} "
                    + $"OUTPUT {Output(rows, keyIndices, "inserted")} "
                    + $"FROM {target} AS t INNER JOIN {staging} AS s "
                    + $"ON {JoinPredicate(rows, conditionIndices)};";
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

        return WithStagingAsync(
            rows, connection, transaction, conditionIndices, keyIndices,
            staging =>
                $"DELETE t OUTPUT {Output(rows, keyIndices, "deleted")} "
                + $"FROM {target} AS t INNER JOIN {staging} AS s "
                + $"ON {JoinPredicate(rows, conditionIndices)};",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> stagedIndices,
        IReadOnlyList<int> keyIndices,
        Func<string, string> buildSql,
        CancellationToken cancellationToken)
    {
        var staging = _sqlHelper.DelimitIdentifier($"#efbulk_{Guid.NewGuid():N}");
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        var columnList = string.Join(
            ", ",
            stagedIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        // Deriving the staging table from the target keeps the column types exactly right without
        // having to translate EF's store types back into DDL.
        await ExecuteNonQueryAsync(
                connection, transaction,
                $"SELECT TOP 0 {columnList} INTO {staging} FROM {target};",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var bulkCopy = _createBulkCopy())
            {
                bulkCopy.DestinationTableName = staging;
                for (var i = 0; i < stagedIndices.Count; i++)
                {
                    bulkCopy.ColumnMappings.Add(i, rows.Columns[stagedIndices[i]].Name);
                }

                await bulkCopy
                    .WriteToServerAsync(
                        new BulkRowSetDataReader(rows, stagedIndices, includeOrdinal: false),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var matched = new HashSet<string>(StringComparer.Ordinal);
            var values = new object?[keyIndices.Count];

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
                        values[i] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                            ? null
                            : reader.GetValue(i);
                    }

                    matched.Add(BulkRowMatching.KeyOf(values));
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

    private string JoinPredicate(IBulkRowSet rows, IReadOnlyList<int> conditionIndices)
        => string.Join(
            " AND ",
            conditionIndices.Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            }));

    private string Output(IBulkRowSet rows, IReadOnlyList<int> keyIndices, string source)
        => string.Join(
            ", ",
            keyIndices.Select(i => $"{source}.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

    private static List<int> Union(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        var result = new List<int>(first);
        foreach (var index in second)
        {
            if (!result.Contains(index))
            {
                result.Add(index);
            }
        }

        return result;
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
