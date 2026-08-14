using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EFToolkit.Bulk.PostgreSQL.Execution;

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
    private readonly Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, bool, CancellationToken, Task> _copyInto;

    private readonly BulkExecutionSettings _settings;
    private readonly int _indexThreshold;

    public NpgsqlBulkWriteOperations(
        ISqlGenerationHelper sqlHelper,
        BulkExecutionSettings settings,
        int indexThreshold,
        Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, bool, CancellationToken, Task> copyInto)
    {
        _sqlHelper = sqlHelper;
        _settings = settings;
        _indexThreshold = indexThreshold;
        _copyInto = copyInto;
    }

    public Task<int> UpdateAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);

        return WithStagingAsync(
            rows, connection, staged, readIndices, conditionIndices,
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

                // RETURNING may name columns of the tables in FROM, which is what lets the source
                // ordinal come back and makes correlation exact.
                return $"UPDATE {target} AS t SET {assignments} FROM {staging} AS s "
                    + $"WHERE {JoinPredicate(rows, conditionIndices, staged)} "
                    + $"RETURNING {Returning(rows, readIndices)}";
            },
            cancellationToken);
    }

    public Task<int> DeleteAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> conditionIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForDelete(rows, conditionIndices);

        return WithStagingAsync(
            rows, connection, staged, [], conditionIndices,
            staging =>
                // RETURNING may likewise name the relation in USING.
                $"DELETE FROM {target} AS t USING {staging} AS s "
                + $"WHERE {JoinPredicate(rows, conditionIndices, staged)} "
                + $"RETURNING {Returning(rows, [])}",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> readIndices,
        IReadOnlyList<int> conditionIndices,
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

        var ordinal = _sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName);

        await ExecuteNonQueryAsync(
                connection,
                $"CREATE TEMP TABLE {staging} AS "
                + $"SELECT {projection}, 0 AS {ordinal} FROM {target} WITH NO DATA",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _copyInto(connection, staging, staged, rows, true, cancellationToken)
                .ConfigureAwait(false);

            var tally = new BulkRowTally(rows.RowCount);

            // ANALYZE and any index are prepended to the statement that needs them, so preparing
            // the staging table costs no extra round trip. Autovacuum never touches a temp table,
            // so without ANALYZE the planner joins it to the target on a hard-coded guess.
            var prelude = Prelude(rows, staging, staged, conditionIndices);

            await using (var command = connection.CreateCommand())
            {
                _settings.Apply(command);
                command.CommandText = prelude + buildSql(staging);

                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                // Advance past the prelude to the statement that returns something. Counting the
                // prelude's statements would be the obvious way and is not reliable — the driver
                // does not promise a result set per statement — whereas only the set-based
                // statement describes any columns, so field count identifies it exactly. A DML
                // statement that matched nothing still describes its RETURNING columns, so this
                // does not confuse "no rows" with "not there yet".
                while (reader.FieldCount == 0
                    && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                {
                }

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // The ordinal leads the RETURNING list, so read columns sit at a fixed offset.
                    var row = reader.GetInt32(0);
                    tally.Mark(row);

                    // Anything the database regenerated -- a concurrency token, a computed column
                    // -- comes back here so the entity ends up matching the row.
                    for (var i = 0; i < readIndices.Count; i++)
                    {
                        var position = 1 + i;
                        var value = await reader.IsDBNullAsync(position, cancellationToken)
                            .ConfigureAwait(false)
                            ? null
                            : reader.GetValue(position);

                        rows.SetGeneratedValue(row, readIndices[i], value);
                    }
                }
            }

            tally.ThrowIfAnyMissing(rows);
            return tally.Count;
        }
        finally
        {
            await StagingCleanup.RunAsync(
                    staging,
                    () => ExecuteNonQueryAsync(
                        connection, $"DROP TABLE IF EXISTS {staging}", CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Builds the statements that precede the set-based statement.</summary>
    private string Prelude(
        IBulkRowSet rows,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> conditionIndices)
    {
        var statements = new List<string>();

        if (StagingPrelude.ShouldIndex(rows.RowCount, conditionIndices.Count, _indexThreshold))
        {
            var columns = string.Join(
                ", ",
                conditionIndices.Select(
                    i => _sqlHelper.DelimitIdentifier(StagedName(staged, i, true))));

            statements.Add($"CREATE INDEX ON {staging} ({columns})");
        }

        statements.Add($"ANALYZE {staging}");

        return string.Join("; ", statements) + "; ";
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

    /// <summary>
    ///     Builds the RETURNING list: the source ordinal, then anything to read back.
    /// </summary>
    /// <remarks>
    ///     Key columns used to lead this list purely so returned rows could be matched to source
    ///     rows by their key values. The ordinal does that directly, so the keys — which the caller
    ///     already has — no longer cross the wire at all.
    /// </remarks>
    private string Returning(IBulkRowSet rows, IReadOnlyList<int> readIndices)
    {
        var parts = new List<string>(readIndices.Count + 1)
        {
            $"s.{_sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName)}"
        };

        parts.AddRange(
            readIndices.Select(i => $"t.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        return string.Join(", ", parts);
    }

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
            $"Column index {index} was not staged, which indicates a bug in EF.Toolkit.Bulk's staging "
            + "layout.");
    }

    private async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
