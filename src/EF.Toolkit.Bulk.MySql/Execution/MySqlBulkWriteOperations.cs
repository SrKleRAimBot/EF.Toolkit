using System.Globalization;
using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.MySql.Execution;

/// <summary>
///     Bulk <c>UPDATE</c> and <c>DELETE</c> on MySQL, via a temporary table joined to the target.
/// </summary>
/// <remarks>
///     <para>
///         The rows are written into a temporary table with multi-row inserts and then applied in a
///         single multi-table statement. MySQL has no <c>OUTPUT</c> clause to report which rows
///         that statement found, so it is asked beforehand instead: one <c>SELECT ... FOR UPDATE</c>
///         joining the same staging table to the target, which returns the ordinal of every source
///         row that will match and locks the rows it found. Holding those locks is what makes the
///         answer still true when the write runs a moment later.
///     </para>
///     <para>
///         Asking first has a useful consequence: a concurrency conflict is known before anything
///         is written, so it is reported without the write ever happening.
///     </para>
/// </remarks>
internal sealed class MySqlBulkWriteOperations
{
    private readonly MySqlSession _session;
    private readonly int _indexThreshold;

    public MySqlBulkWriteOperations(MySqlSession session, int indexThreshold)
    {
        _session = session;
        _indexThreshold = indexThreshold;
    }

    public Task<int> UpdateAsync(
        IBulkRowSet rows,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var staged = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);
        var target = _session.Target(rows);

        return WithStagingAsync(
            rows, staged, conditionIndices,
            async (staging, tally) =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                        $"t.{_session.Quote(rows.Columns[i].Name)} = "
                        + $"s.{_session.Quote(StagedName(staged, i, false))}"));

                await _session
                    .ExecuteNonQueryAsync(
                        $"UPDATE {target} AS t INNER JOIN {staging} AS s "
                        + $"ON {JoinPredicate(rows, conditionIndices, staged)} SET {assignments}",
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (readIndices.Count > 0)
                {
                    await ReadBackAsync(
                            rows, staging, staged, writeIndices, conditionIndices, readIndices,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            },
            cancellationToken);
    }

    public Task<int> DeleteAsync(
        IBulkRowSet rows,
        IReadOnlyList<int> conditionIndices,
        CancellationToken cancellationToken)
    {
        var staged = StagingColumn.ForDelete(rows, conditionIndices);
        var target = _session.Target(rows);

        return WithStagingAsync(
            rows, staged, conditionIndices,
            (staging, _) => _session.ExecuteNonQueryAsync(
                $"DELETE t FROM {target} AS t INNER JOIN {staging} AS s "
                + $"ON {JoinPredicate(rows, conditionIndices, staged)}",
                null,
                cancellationToken),
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        List<StagingColumn> staged,
        IReadOnlyList<int> conditionIndices,
        Func<string, BulkRowTally, Task> write,
        CancellationToken cancellationToken)
    {
        var staging = _session.NewStagingName();

        await _session
            .CreateStagingAsync(
                rows, staging, staged, includeOrdinal: true,
                MySqlSession.IndexFor(
                    rows, conditionIndices, i => StagedName(staged, i, true), _indexThreshold),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _session
                .InsertRowsAsync(
                    staging,
                    StagedColumnList(staged),
                    rows,
                    staged,
                    includeOrdinal: true,
                    MySqlBulkExecutor.AllRows(rows.RowCount),
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);

            var tally = new BulkRowTally(rows.RowCount);

            await LocateAsync(rows, staging, staged, conditionIndices, tally, cancellationToken)
                .ConfigureAwait(false);

            tally.ThrowIfAnyMissing(rows);

            await write(staging, tally).ConfigureAwait(false);

            return tally.Count;
        }
        finally
        {
            await _session.DropStagingAsync(staging).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Finds and locks every target row the write will change, recording which source rows
    ///     found one — and, when asked for, what each held.
    /// </summary>
    private async Task LocateAsync(
        IBulkRowSet rows,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> conditionIndices,
        BulkRowTally tally,
        CancellationToken cancellationToken)
    {
        var beforeImages = rows.BeforeImages;
        var ordinal = _session.Quote(StagingColumn.OrdinalColumnName);

        var projection = beforeImages is null
            ? ""
            : ", " + string.Join(", ", beforeImages.Columns.Select(c => $"t.{_session.Quote(c.Name)}"));

        await using var command = _session.CreateCommand(
            $"SELECT s.{ordinal}{projection} FROM {staging} AS s "
            + $"INNER JOIN {_session.Target(rows)} AS t "
            + $"ON {JoinPredicate(rows, conditionIndices, staged)} FOR UPDATE");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        // A source row that locates the same target row as an earlier one is left unmarked, and so
        // reported as unaccounted for: the join applies the pair once, and the engines with an
        // OUTPUT or RETURNING clause see exactly that.
        var repeats = BulkRowMatching.Repeats(rows, conditionIndices, original: true);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

            if (repeats?[row] == true)
            {
                continue;
            }

            tally.Mark(row);

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
    }

    /// <summary>
    ///     Reads back what the database regenerated during the update — an <c>ON UPDATE</c>
    ///     timestamp, a computed column — so the entity ends up matching the row.
    /// </summary>
    /// <remarks>
    ///     The target is located again by the condition columns as they stand <em>after</em> the
    ///     update: the new value of anything the update assigned, the loaded value of anything it
    ///     did not, and nothing the database itself has just changed.
    /// </remarks>
    private async Task ReadBackAsync(
        IBulkRowSet rows,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var predicate = string.Join(
            " AND ",
            conditionIndices
                .Where(i => !readIndices.Contains(i))
                .Select(i =>
                {
                    var name = writeIndices.Contains(i)
                        ? StagedName(staged, i, false)
                        : StagedName(staged, i, true);

                    return $"t.{_session.Quote(rows.Columns[i].Name)} = s.{_session.Quote(name)}";
                }));

        var ordinal = _session.Quote(StagingColumn.OrdinalColumnName);
        var reads = readIndices.Select(i => rows.Columns[i]).ToList();
        var generated = new BulkGeneratedValues();

        await using var command = _session.CreateCommand(
            $"SELECT s.{ordinal}, {_session.ColumnList(rows, readIndices, "t")} FROM {staging} AS s "
            + $"INNER JOIN {_session.Target(rows)} AS t ON {predicate}");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture);

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

        generated.ApplyTo(rows);
    }

    private string StagedColumnList(IReadOnlyList<StagingColumn> staged)
        => string.Join(
            ", ",
            staged.Select(c => _session.Quote(c.Name))
                .Append(_session.Quote(StagingColumn.OrdinalColumnName)));

    private string JoinPredicate(
        IBulkRowSet rows,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<StagingColumn> staged)
        => string.Join(
            " AND ",
            conditionIndices.Select(i =>
                $"t.{_session.Quote(rows.Columns[i].Name)} = "
                + $"s.{_session.Quote(StagedName(staged, i, true))}"));

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
            $"Column index {index} was not staged, which indicates a bug in EF.Toolkit.Bulk's "
            + "staging layout.");
    }
}
