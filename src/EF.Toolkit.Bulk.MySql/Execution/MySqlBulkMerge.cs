using System.Globalization;
using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.MySql.Execution;

/// <summary>
///     Upserts on MySQL: stage the rows, find which already exist, update those in one joined
///     statement and insert the rest.
/// </summary>
/// <remarks>
///     <para>
///         <c>INSERT ... ON DUPLICATE KEY UPDATE</c> is the obvious statement and the wrong one. It
///         fires on a clash with <em>any</em> unique index or the primary key, not with the columns
///         the caller chose to match on — a merge by email would update whichever row's surrogate
///         key happened to collide instead — and its affected-row count cannot say which rows were
///         inserted and which updated. It also reports no generated key for an updated row.
///     </para>
///     <para>
///         Finding the existing rows first answers all of that. One <c>SELECT ... FOR UPDATE</c>
///         joining the staging table to the target says, per source row, whether it matched —
///         which makes the insert-versus-update split exact — and locks what it found, so the
///         answer still holds when the update runs. Under InnoDB's default isolation the locking
///         read also takes gap locks over the match columns' index, which keeps a concurrent session
///         from inserting a matching row in between; without an index on the match columns that
///         protection degrades to a lock over the whole scan, which is correct but contended.
///     </para>
///     <para>
///         The rows that did not match are inserted from the application rather than from the
///         staging table — they would cross the wire either way — and then every written row's
///         generated values are read back in one join on the match columns, which find an inserted
///         row as surely as an updated one.
///     </para>
/// </remarks>
internal sealed class MySqlBulkMerge
{
    private readonly MySqlSession _session;
    private readonly int _indexThreshold;

    public MySqlBulkMerge(MySqlSession session, int indexThreshold)
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

        // A join would apply two source rows with the same match values to one target row in some
        // unstated order, or insert both; the engines with a MERGE statement refuse it instead.
        BulkRowMatching.ThrowIfMergeSourceRepeats(rows, matchIndices);

        var target = _session.Target(rows);
        var staging = _session.NewStagingName();
        var staged = StagingColumn.ForWrite(rows, writeIndices);
        var join = _session.Join(rows, matchIndices);

        await _session
            .CreateStagingAsync(
                rows, staging, staged, includeOrdinal: true,
                MySqlSession.IndexFor(rows, matchIndices, i => rows.Columns[i].Name, _indexThreshold),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _session
                .InsertRowsAsync(
                    staging,
                    string.Join(
                        ", ",
                        staged.Select(c => _session.Quote(c.Name))
                            .Append(_session.Quote(StagingColumn.OrdinalColumnName))),
                    rows,
                    staged,
                    includeOrdinal: true,
                    MySqlBulkExecutor.AllRows(rows.RowCount),
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            var (matched, matchedTargets) = await LocateAsync(rows, staging, join, cancellationToken)
                .ConfigureAwait(false);

            var deleted = deleteMissing
                ? await DeleteMissingAsync(rows, staging, target, join, cancellationToken)
                    .ConfigureAwait(false)
                : 0;

            var generated = new BulkGeneratedValues();
            var updated = 0;

            // Match columns identify the row, so they are never themselves reassigned, and an
            // insert-only column is written by the insert arm alone.
            var assignments = writeIndices
                .Where(i => !matchIndices.Contains(i) && !rows.Columns[i].IsInsertOnly)
                .Select(i =>
                {
                    var column = _session.Quote(rows.Columns[i].Name);
                    return $"t.{column} = s.{column}";
                })
                .ToList();

            // With nothing to assign, a matched row is left alone and not counted — exactly as a
            // MERGE with no WHEN MATCHED arm leaves it.
            if (assignments.Count > 0 && matchedTargets > 0)
            {
                await _session
                    .ExecuteNonQueryAsync(
                        $"UPDATE {target} AS t INNER JOIN {staging} AS s ON {join} "
                        + $"SET {string.Join(", ", assignments)}",
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                updated = matchedTargets;
            }

            var unmatched = new List<int>(rows.RowCount);
            for (var row = 0; row < rows.RowCount; row++)
            {
                if (!matched[row])
                {
                    unmatched.Add(row);
                }
            }

            if (unmatched.Count > 0)
            {
                await _session
                    .InsertRowsAsync(
                        target,
                        _session.ColumnList(rows, writeIndices),
                        rows,
                        staged,
                        includeOrdinal: false,
                        unmatched,
                        null,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (readIndices.Count > 0)
            {
                // A row the update left alone reports nothing back, as MERGE without a WHEN MATCHED
                // arm reports nothing for it.
                var written = new bool[rows.RowCount];
                for (var row = 0; row < rows.RowCount; row++)
                {
                    written[row] = !matched[row] || assignments.Count > 0;
                }

                await ReadBackAsync(rows, staging, join, readIndices, written, generated, cancellationToken)
                    .ConfigureAwait(false);
            }

            generated.ApplyTo(rows);
            return (unmatched.Count, updated, deleted);
        }
        finally
        {
            await _session.DropStagingAsync(staging).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Finds and locks the target rows the source matches, recording which source rows found
    ///     one and, when asked for, what each held.
    /// </summary>
    /// <returns>Which source rows matched, and how many target rows they matched between them.</returns>
    private async Task<(bool[] Matched, int Targets)> LocateAsync(
        IBulkRowSet rows,
        string staging,
        string join,
        CancellationToken cancellationToken)
    {
        var beforeImages = rows.BeforeImages;
        var ordinal = _session.Quote(StagingColumn.OrdinalColumnName);

        var projection = beforeImages is null
            ? ""
            : ", " + string.Join(", ", beforeImages.Columns.Select(c => $"t.{_session.Quote(c.Name)}"));

        await using var command = _session.CreateCommand(
            $"SELECT s.{ordinal}{projection} FROM {staging} AS s "
            + $"INNER JOIN {_session.Target(rows)} AS t ON {join} FOR UPDATE");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        var matched = new bool[rows.RowCount];
        var targets = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);
            matched[row] = true;
            targets++;

            if (beforeImages is null)
            {
                continue;
            }

            for (var i = 0; i < beforeImages.Columns.Count; i++)
            {
                beforeImages.SetValue(
                    row,
                    i,
                    await BulkValueReader
                        .ReadAsync(reader, 1 + i, beforeImages.Columns[i], cancellationToken)
                        .ConfigureAwait(false));
            }
        }

        return (matched, targets);
    }

    /// <summary>
    ///     Deletes the target rows the source does not contain, within the scope if there is one.
    /// </summary>
    /// <remarks>
    ///     Runs before the insert, so the rows it reports and removes are exactly the ones that
    ///     were there to remove; every source row is in the staging table it is tested against, so
    ///     nothing the merge is about to write can be reached by it.
    /// </remarks>
    private async Task<int> DeleteMissingAsync(
        IBulkRowSet rows,
        string staging,
        string target,
        string join,
        CancellationToken cancellationToken)
    {
        // The scope fences which target rows the delete may reach at all, so it is ANDed onto the
        // "not in the source" test rather than replacing it.
        var predicate = $"NOT EXISTS (SELECT 1 FROM {staging} AS s WHERE {join})"
            + (rows.Scope is { } fence ? $" AND ({fence.Sql})" : "");

        if (rows.BeforeImages is { } beforeImages)
        {
            var projection = string.Join(
                ", ", beforeImages.Columns.Select(c => $"t.{_session.Quote(c.Name)}"));

            await _session
                .CaptureRemovedAsync(
                    $"SELECT {projection} FROM {target} AS t WHERE {predicate} FOR UPDATE",
                    rows.Scope, beforeImages, cancellationToken)
                .ConfigureAwait(false);
        }

        // The multi-table form, which every supported MySQL accepts with an alias; the single-table
        // DELETE only took one from 8.0.16.
        return await _session
            .ExecuteNonQueryAsync(
                $"DELETE t FROM {target} AS t WHERE {predicate}", rows.Scope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads back the generated values of every row the merge wrote — a store-generated key
    ///     above all, which a caller merging by a natural key has no other way to learn.
    /// </summary>
    /// <remarks>
    ///     Inserted and updated rows alike are found again by the match columns, which is what
    ///     identifies a row to a merge in the first place. That needs no <c>LAST_INSERT_ID()</c>
    ///     arithmetic, so it holds under every AUTO_INCREMENT lock mode, and it reads back
    ///     defaults and computed columns as readily as keys.
    /// </remarks>
    private async Task ReadBackAsync(
        IBulkRowSet rows,
        string staging,
        string join,
        List<int> readIndices,
        bool[] written,
        BulkGeneratedValues generated,
        CancellationToken cancellationToken)
    {
        var ordinal = _session.Quote(StagingColumn.OrdinalColumnName);
        var reads = readIndices.Select(i => rows.Columns[i]).ToList();

        await using var command = _session.CreateCommand(
            $"SELECT s.{ordinal}, {_session.ColumnList(rows, readIndices, "t")} FROM {staging} AS s "
            + $"INNER JOIN {_session.Target(rows)} AS t ON {join}");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

            if (!written[row])
            {
                continue;
            }

            for (var i = 0; i < reads.Count; i++)
            {
                generated.Add(
                    row,
                    readIndices[i],
                    await BulkValueReader
                        .ReadAsync(reader, 1 + i, reads[i], cancellationToken)
                        .ConfigureAwait(false));
            }
        }
    }
}
