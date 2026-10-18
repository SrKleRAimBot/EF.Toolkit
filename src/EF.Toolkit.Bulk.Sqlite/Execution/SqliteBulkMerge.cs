using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.Sqlite.Execution;

/// <summary>
///     Upserts on SQLite: an <c>UPDATE</c> located by the match columns, and an <c>INSERT</c> for
///     every row it did not find.
/// </summary>
/// <remarks>
///     <para>
///         <c>INSERT ... ON CONFLICT DO UPDATE</c> is the obvious statement and the wrong one. A
///         conflict there means a clash on <em>any</em> unique index or primary key, not on the
///         columns the caller chose to match on, so a merge by email would quietly update the row
///         whose surrogate key happened to collide instead. It also needs a unique index over the
///         match columns to exist at all, which the other engines' <c>MERGE</c> does not.
///     </para>
///     <para>
///         Update-then-insert has neither problem, and per row it costs SQLite nothing extra: both
///         statements are prepared once, the update reports from its own affected-row count
///         whether the row existed, and the insert-versus-update split comes out exact for free.
///         SQLite admits one writer at a time, so no other session can insert the row between the
///         two.
///     </para>
/// </remarks>
internal sealed class SqliteBulkMerge
{
    private readonly SqliteSession _session;
    private readonly int _indexThreshold;

    public SqliteBulkMerge(SqliteSession session, int indexThreshold)
    {
        _session = session;
        _indexThreshold = indexThreshold;
    }

    public async Task<(int Inserted, int Updated, int Deleted)> ExecuteAsync(
        IBulkRowSet rows,
        bool deleteMissing,
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

        BulkRowMatching.ThrowIfMergeSourceRepeats(rows, matchIndices);

        // The rows carry what the caller supplied, so the match is on current values throughout.
        var matchLayout = StagingColumn.ForWrite(rows, matchIndices);

        if (rows.BeforeImages is { } beforeImages)
        {
            await SqliteBulkWriteOperations
                .CaptureBeforeAsync(
                    _session, rows, matchIndices, matchLayout, beforeImages, cancellationToken)
                .ConfigureAwait(false);
        }

        // Removing what the source omitted goes first. It cannot reach a source row -- every one is
        // in the staged set it is tested against -- and running it before the upsert means the
        // rows captured as removed are exactly the rows that were there to remove.
        var deleted = deleteMissing
            ? await DeleteMissingAsync(rows, matchIndices, matchLayout, cancellationToken)
                .ConfigureAwait(false)
            : 0;

        var (inserted, updated) = await UpsertAsync(
                rows, writeIndices, matchIndices, readIndices, matchLayout, cancellationToken)
            .ConfigureAwait(false);

        return (inserted, updated, deleted);
    }

    private async Task<(int Inserted, int Updated)> UpsertAsync(
        IBulkRowSet rows,
        List<int> writeIndices,
        List<int> matchIndices,
        List<int> readIndices,
        IReadOnlyList<StagingColumn> matchLayout,
        CancellationToken cancellationToken)
    {
        var target = _session.Target(rows);
        var returning = readIndices.Count == 0
            ? ""
            : $" RETURNING {_session.ColumnList(rows, readIndices)}";

        // Match columns identify the row, so they are never themselves reassigned, and an
        // insert-only column is written by the insert arm alone.
        var assigned = writeIndices
            .Where(i => !matchIndices.Contains(i) && !rows.Columns[i].IsInsertOnly)
            .ToList();

        // With nothing to assign there is nothing to update, only something to find -- and a found
        // row is left alone and not counted, exactly as a MERGE with no WHEN MATCHED arm leaves it.
        var locate = assigned.Count == 0
            ? _session.Statement(
                $"SELECT 1 FROM {target} WHERE {_session.Predicate(rows, matchIndices, 0)} LIMIT 1",
                rows,
                matchLayout)
            : _session.Statement(
                $"UPDATE {target} SET "
                + string.Join(
                    ", ",
                    assigned.Select((index, i) =>
                        $"{_session.Quote(rows.Columns[index].Name)} = {SqliteRowStatement.Parameter(i)}"))
                + $" WHERE {_session.Predicate(rows, matchIndices, assigned.Count)}{returning}",
                rows,
                [.. StagingColumn.ForWrite(rows, assigned), .. matchLayout]);

        var insertLayout = StagingColumn.ForWrite(rows, writeIndices);
        var insertSql = writeIndices.Count == 0
            ? $"INSERT INTO {target} DEFAULT VALUES{returning}"
            : $"INSERT INTO {target} ({_session.ColumnList(rows, writeIndices)}) VALUES "
                + $"({SqliteSession.Parameters(insertLayout.Count)})"
                + returning;

        var reads = readIndices.Select(i => rows.Columns[i]).ToList();
        var generated = new BulkGeneratedValues();
        var inserted = 0;
        var updated = 0;

        await using (locate)
        await using (var insert = _session.Statement(insertSql, rows, insertLayout))
        {
            for (var row = 0; row < rows.RowCount; row++)
            {
                var current = row;

                if (assigned.Count == 0)
                {
                    await using var probe = await locate.ExecuteReaderAsync(row, cancellationToken)
                        .ConfigureAwait(false);

                    if (await probe.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
                else
                {
                    int found;

                    if (readIndices.Count == 0)
                    {
                        found = await locate.ExecuteNonQueryAsync(row, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await using var reader = await locate.ExecuteReaderAsync(row, cancellationToken)
                            .ConfigureAwait(false);

                        found = await BulkCommandSession
                            .ReadAllAsync(
                                reader, reads, 0,
                                (i, value) => generated.Add(current, readIndices[i], value),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (found > 0)
                    {
                        // A non-unique match can update several rows for one source row, and each
                        // of them counts, as each is reported by MERGE.
                        updated += found;
                        continue;
                    }
                }

                if (readIndices.Count == 0)
                {
                    await insert.ExecuteNonQueryAsync(row, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var reader = await insert.ExecuteReaderAsync(row, cancellationToken)
                        .ConfigureAwait(false);

                    await BulkCommandSession
                        .ReadAllAsync(
                            reader, reads, 0,
                            (i, value) => generated.Add(current, readIndices[i], value),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                inserted++;
            }
        }

        generated.ApplyTo(rows);
        return (inserted, updated);
    }

    /// <summary>
    ///     Deletes the target rows the source does not contain, within the scope if there is one.
    /// </summary>
    /// <remarks>
    ///     The one part of a synchronise that is a set rather than a row at a time, because it is
    ///     about rows nobody passed in. The match values are staged into a temporary table so the
    ///     delete can test "not in the source" in a single statement.
    /// </remarks>
    private async Task<int> DeleteMissingAsync(
        IBulkRowSet rows,
        List<int> matchIndices,
        List<StagingColumn> matchLayout,
        CancellationToken cancellationToken)
    {
        var target = _session.Target(rows);
        var name = $"efbulk_{Guid.NewGuid():N}";
        var staging = $"temp.{_session.Quote(name)}";
        var columns = _session.ColumnList(rows, matchIndices);

        // CREATE TABLE ... AS SELECT carries each column's affinity across, so the comparison below
        // converts values the same way a comparison against the target column itself would.
        await _session
            .ExecuteNonQueryAsync(
                $"CREATE TEMP TABLE {_session.Quote(name)} AS SELECT {columns} FROM {target} WHERE 0",
                null,
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await using (var load = _session.Statement(
                $"INSERT INTO {staging} ({columns}) VALUES "
                + $"({SqliteSession.Parameters(matchLayout.Count)})",
                rows,
                matchLayout))
            {
                for (var row = 0; row < rows.RowCount; row++)
                {
                    await load.ExecuteNonQueryAsync(row, cancellationToken).ConfigureAwait(false);
                }
            }

            if (StagingPrelude.ShouldIndex(rows.RowCount, matchIndices.Count, _indexThreshold))
            {
                await _session
                    .ExecuteNonQueryAsync(
                        $"CREATE INDEX temp.{_session.Quote(name + "_ix")} "
                        + $"ON {_session.Quote(name)} ({columns}); ANALYZE {staging}",
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // The scope fences which target rows the delete may reach at all, so it is ANDed onto
            // the "not in the source" test rather than replacing it.
            var predicate = $"NOT EXISTS (SELECT 1 FROM {staging} AS s WHERE {_session.Join(rows, matchIndices)})"
                + (rows.Scope is { } fence ? $" AND ({fence.Sql})" : "");

            if (rows.BeforeImages is { } beforeImages)
            {
                var projection = string.Join(
                    ", ", beforeImages.Columns.Select(c => $"t.{_session.Quote(c.Name)}"));

                await _session
                    .CaptureRemovedAsync(
                        $"SELECT {projection} FROM {target} AS t WHERE {predicate}",
                        rows.Scope, beforeImages, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _session
                .ExecuteNonQueryAsync(
                    $"DELETE FROM {target} AS t WHERE {predicate}", rows.Scope, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await StagingCleanup.RunAsync(
                    staging,
                    () => _session.ExecuteNonQueryAsync(
                        $"DROP TABLE IF EXISTS {staging}", null, CancellationToken.None))
                .ConfigureAwait(false);
        }
    }
}
