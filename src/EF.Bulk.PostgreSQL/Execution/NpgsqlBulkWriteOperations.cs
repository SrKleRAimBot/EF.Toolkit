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
    private readonly Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, CancellationToken, Task> _copyInto;

    public NpgsqlBulkWriteOperations(
        ISqlGenerationHelper sqlHelper,
        Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, CancellationToken, Task> copyInto)
    {
        _sqlHelper = sqlHelper;
        _copyInto = copyInto;
    }

    public Task<int> UpdateAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);

        return WithStagingAsync(
            rows, connection, staged, keyIndices, readIndices,
            staging =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        var source = _sqlHelper.DelimitIdentifier(StagedName(staged, i, false));
                        return $"{column} = s.{source}";
                    }));

                return $"UPDATE {target} AS t SET {assignments} FROM {staging} AS s "
                    + $"WHERE {JoinPredicate(rows, conditionIndices, staged)} "
                    + $"RETURNING {Returning(rows, keyIndices, readIndices)}";
            },
            cancellationToken);
    }

    public Task<int> DeleteAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> keyIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForDelete(rows, conditionIndices);

        return WithStagingAsync(
            rows, connection, staged, keyIndices, [],
            staging =>
                $"DELETE FROM {target} AS t USING {staging} AS s "
                + $"WHERE {JoinPredicate(rows, conditionIndices, staged)} "
                + $"RETURNING {Returning(rows, keyIndices, [])}",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices,
        Func<string, string> buildSql,
        CancellationToken cancellationToken)
    {
        var staging = _sqlHelper.DelimitIdentifier($"efbulk_{Guid.NewGuid():N}");
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

        // Aliased so a column staged twice -- a concurrency token's loaded and new values -- gets
        // two distinct staging columns of the correct type.
        var projection = string.Join(
            ", ",
            staged.Select(c =>
                $"{_sqlHelper.DelimitIdentifier(rows.Columns[c.Index].Name)} AS "
                + $"{_sqlHelper.DelimitIdentifier(c.Name)}"));

        await ExecuteNonQueryAsync(
                connection,
                $"CREATE TEMP TABLE {staging} AS SELECT {projection} FROM {target} WITH NO DATA",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _copyInto(connection, staging, staged, rows, cancellationToken)
                .ConfigureAwait(false);

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
            // Best effort. If the statement above failed, PostgreSQL has aborted the transaction
            // and will reject this too -- and the temp table dies with the rollback regardless.
            // Letting it throw here would replace the real error with a misleading one.
            try
            {
                await ExecuteNonQueryAsync(
                        connection, $"DROP TABLE IF EXISTS {staging}", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (PostgresException)
            {
            }
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

    private string Returning(
        IBulkRowSet rows,
        IReadOnlyList<int> keyIndices,
        IReadOnlyList<int> readIndices)
        => string.Join(
            ", ",
            keyIndices.Concat(readIndices)
                .Select(i => $"t.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

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
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
