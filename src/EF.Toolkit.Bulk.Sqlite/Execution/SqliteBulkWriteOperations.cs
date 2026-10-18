using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.Sqlite.Execution;

/// <summary>
///     Bulk <c>UPDATE</c> and <c>DELETE</c> on SQLite, one prepared statement per operation.
/// </summary>
/// <remarks>
///     There is no staging table here, unlike on the networked engines. Staging exists there to
///     turn many statements into one, and in-process that buys nothing — whereas a statement per row
///     reports its own affected-row count, so a row that has gone missing is known by position the
///     moment it happens rather than recovered afterwards from an <c>OUTPUT</c> list.
/// </remarks>
internal sealed class SqliteBulkWriteOperations
{
    private readonly SqliteSession _session;

    public SqliteBulkWriteOperations(SqliteSession session) => _session = session;

    public async Task<int> UpdateAsync(
        IBulkRowSet rows,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        // Conditions first, carrying the loaded values, then the written values: a concurrency
        // token is in both lists, located by what was read and assigned what is new.
        var layout = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);

        var assignments = string.Join(
            ", ",
            writeIndices.Select((index, i) =>
                $"{_session.Quote(rows.Columns[index].Name)} = "
                + SqliteRowStatement.Parameter(conditionIndices.Count + i)));

        var sql = $"UPDATE {_session.Target(rows)} SET {assignments} "
            + $"WHERE {_session.Predicate(rows, conditionIndices, 0)}";

        if (rows.BeforeImages is { } beforeImages)
        {
            await CaptureBeforeAsync(
                    _session, rows, conditionIndices, StagingColumn.ForDelete(rows, conditionIndices),
                    beforeImages, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ApplyAsync(rows, sql, layout, conditionIndices, readIndices, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(
        IBulkRowSet rows,
        IReadOnlyList<int> conditionIndices,
        CancellationToken cancellationToken)
    {
        var layout = StagingColumn.ForDelete(rows, conditionIndices);

        if (rows.BeforeImages is { } beforeImages)
        {
            await CaptureBeforeAsync(
                    _session, rows, conditionIndices, layout, beforeImages, cancellationToken)
                .ConfigureAwait(false);
        }

        return await ApplyAsync(
                rows,
                $"DELETE FROM {_session.Target(rows)} "
                + $"WHERE {_session.Predicate(rows, conditionIndices, 0)}",
                layout, conditionIndices, [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads every row the operation is about to change, as it stands now.
    /// </summary>
    /// <remarks>
    ///     All of them, before the first write, rather than interleaved with the writes: two source
    ///     rows that locate the same target row must both see it as it was, not one of them as the
    ///     other left it. SQLite admits one writer at a time, so nothing else can change a row
    ///     between this read and the statement that follows.
    /// </remarks>
    /// <param name="session">The operation's connection and settings.</param>
    /// <param name="rows">The rows being written.</param>
    /// <param name="locateBy">The columns that locate a target row.</param>
    /// <param name="layout">The values bound to <paramref name="locateBy" />, in the same order.</param>
    /// <param name="beforeImages">Where to record what was found.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task CaptureBeforeAsync(
        SqliteSession session,
        IBulkRowSet rows,
        IReadOnlyList<int> locateBy,
        IReadOnlyList<StagingColumn> layout,
        BulkBeforeImages beforeImages,
        CancellationToken cancellationToken)
    {
        var projection = string.Join(", ", beforeImages.Columns.Select(c => session.Quote(c.Name)));

        await using var select = session.Statement(
            $"SELECT {projection} FROM {session.Target(rows)} "
            + $"WHERE {session.Predicate(rows, locateBy, 0)}",
            rows,
            layout);

        for (var row = 0; row < rows.RowCount; row++)
        {
            await using var reader = await select.ExecuteReaderAsync(row, cancellationToken)
                .ConfigureAwait(false);

            var current = row;
            await BulkCommandSession
                .ReadAllAsync(
                    reader, beforeImages.Columns, 0,
                    (i, value) => beforeImages.SetValue(current, i, value),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Runs <paramref name="sql" /> for every row, tallying the rows it found.</summary>
    /// <remarks>
    ///     A source row that locates the same target row as an earlier one is not run at all, and so
    ///     is reported as unaccounted for — which is what the set-based engines report, because their
    ///     join applies the pair once.
    /// </remarks>
    private async Task<int> ApplyAsync(
        IBulkRowSet rows,
        string sql,
        IReadOnlyList<StagingColumn> layout,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var tally = new BulkRowTally(rows.RowCount);
        var repeats = BulkRowMatching.Repeats(rows, conditionIndices, original: true);
        var generated = new BulkGeneratedValues();
        var reads = readIndices.Select(i => rows.Columns[i]).ToList();

        await using (var statement = _session.Statement(
            readIndices.Count == 0
                ? sql
                : $"{sql} RETURNING {_session.ColumnList(rows, readIndices)}",
            rows,
            layout))
        {
            for (var row = 0; row < rows.RowCount; row++)
            {
                if (repeats?[row] == true)
                {
                    continue;
                }

                int found;

                if (readIndices.Count == 0)
                {
                    found = await statement.ExecuteNonQueryAsync(row, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // Anything the database regenerated -- a computed column, a trigger-maintained
                    // token -- comes back here so the entity ends up matching the row.
                    await using var reader = await statement.ExecuteReaderAsync(row, cancellationToken)
                        .ConfigureAwait(false);

                    var current = row;
                    found = await BulkCommandSession
                        .ReadAllAsync(
                            reader, reads, 0,
                            (i, value) => generated.Add(current, readIndices[i], value),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (found > 0)
                {
                    tally.Mark(row);
                }
            }
        }

        tally.ThrowIfAnyMissing(rows);
        generated.ApplyTo(rows);

        return tally.Count;
    }
}
