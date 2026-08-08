using EFBulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EFBulk.PostgreSQL.Execution;

/// <summary>
///     Bulk <c>UPDATE</c> and <c>DELETE</c> on PostgreSQL, via a temporary table joined to the
///     target.
/// </summary>
/// <remarks>
///     <para>
///         The rows are streamed into a temporary table with binary <c>COPY</c> and then applied in
///         a single set-based statement, turning N round trips into two.
///     </para>
///     <para>
///         Both statements end in <c>RETURNING</c> so the keys actually matched come back. That
///         matters because a bulk statement reports one affected-row count for the whole set, while
///         EF's per-row statements can say precisely which row went missing — recovering that
///         detail is what lets a concurrency conflict name the entities involved.
///     </para>
/// </remarks>
internal sealed class NpgsqlBulkWriteOperations
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly Func<NpgsqlConnection, string, IReadOnlyList<int>, IBulkRowSet, CancellationToken, Task> _copyInto;

    public NpgsqlBulkWriteOperations(
        ISqlGenerationHelper sqlHelper,
        Func<NpgsqlConnection, string, IReadOnlyList<int>, IBulkRowSet, CancellationToken, Task> copyInto)
    {
        _sqlHelper = sqlHelper;
        _copyInto = copyInto;
    }

    public async Task<int> UpdateAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = Union(conditionIndices, writeIndices);

        return await WithStagingAsync(
            rows, connection, staged, keyIndices,
            staging =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        return $"{column} = s.{column}";
                    }));

                var sql = $"UPDATE {target} AS t SET {assignments} FROM {staging} AS s "
                    + $"WHERE {JoinPredicate(rows, conditionIndices)} "
                    + $"RETURNING {Returning(rows, keyIndices, "t")}";

                return sql;
            },
            cancellationToken);
    }

    public async Task<int> DeleteAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        return await WithStagingAsync(
            rows, connection, conditionIndices, keyIndices,
            staging =>
                $"DELETE FROM {target} AS t USING {staging} AS s "
                + $"WHERE {JoinPredicate(rows, conditionIndices)} "
                + $"RETURNING {Returning(rows, keyIndices, "t")}",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> stagedIndices,
        IReadOnlyList<int> keyIndices,
        Func<string, string> buildSql,
        CancellationToken cancellationToken)
    {
        var staging = _sqlHelper.DelimitIdentifier($"efbulk_{Guid.NewGuid():N}");
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        var columnList = string.Join(
            ", ",
            stagedIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        // Deriving the staging table from the target keeps the column types exactly right without
        // having to translate EF's store types back into DDL.
        await ExecuteNonQueryAsync(
                connection,
                $"CREATE TEMP TABLE {staging} AS SELECT {columnList} FROM {target} WITH NO DATA",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _copyInto(connection, staging, stagedIndices, rows, cancellationToken)
                .ConfigureAwait(false);

            var matched = new HashSet<string>(StringComparer.Ordinal);
            var values = new object?[keyIndices.Count];

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = buildSql(staging);

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
            // Temp tables live as long as the session, and Npgsql pools sessions, so one left
            // behind would outlive the operation that created it.
            await ExecuteNonQueryAsync(
                    connection, $"DROP TABLE IF EXISTS {staging}", CancellationToken.None)
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

    private string Returning(IBulkRowSet rows, IReadOnlyList<int> keyIndices, string alias)
        => string.Join(
            ", ",
            keyIndices.Select(i => $"{alias}.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

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
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
