using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EFToolkit.Bulk.PostgreSQL.Execution;

/// <summary>
///     Upserts on PostgreSQL using <c>INSERT ... ON CONFLICT ... DO UPDATE</c>.
/// </summary>
/// <remarks>
///     <para>
///         The rows are staged with binary <c>COPY</c> and then inserted from the staging table in
///         one statement, so the database decides insert-versus-update per row rather than the
///         application doing a read followed by a write.
///     </para>
///     <para>
///         <c>ON CONFLICT</c> needs a unique index over the match columns — that is what defines a
///         conflict — so a merge on columns without one is rejected by the server rather than
///         silently inserting duplicates.
///     </para>
/// </remarks>
internal sealed class NpgsqlBulkMerge
{
    private readonly ISqlGenerationHelper _sqlHelper;
    private readonly Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, bool, CancellationToken, Task> _copyInto;

    private readonly BulkExecutionSettings _settings;
    private readonly bool? _useMerge;

    public NpgsqlBulkMerge(
        ISqlGenerationHelper sqlHelper,
        BulkExecutionSettings settings,
        bool? useMerge,
        Func<NpgsqlConnection, string, IReadOnlyList<StagingColumn>, IBulkRowSet, bool, CancellationToken, Task> copyInto)
    {
        _sqlHelper = sqlHelper;
        _settings = settings;
        _useMerge = useMerge;
        _copyInto = copyInto;
    }

    public async Task<(int Inserted, int Updated, int Deleted)> ExecuteAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
        bool deleteMissing,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staging = _sqlHelper.DelimitIdentifier($"efbulk_{Guid.NewGuid():N}");
        var ordinalColumn = _sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName);

        var useMerge = SupportsMerge(connection);

        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        // MERGE's RETURNING can name the source, so the ordinal is worth staging there; ON CONFLICT
        // cannot, so staging it would be dead weight on every row. A before-image read joins the
        // staging table itself and needs the ordinal to correlate what it finds, so it earns its
        // place on either path when one is being taken.
        var stageOrdinal = useMerge || rows.BeforeImages is not null;
        var ordinalProjection = stageOrdinal ? $", 0 AS {ordinalColumn}" : "";

        await ExecuteNonQueryAsync(
                connection,
                $"CREATE TEMP TABLE {staging} AS "
                + $"SELECT {columnList}{ordinalProjection} FROM {target} WITH NO DATA",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _copyInto(
                    connection, staging, StagingColumn.ForWrite(rows, writeIndices), rows,
                    stageOrdinal, cancellationToken)
                .ConfigureAwait(false);

            if (rows.BeforeImages is { } beforeImages)
            {
                await CaptureBeforeAsync(
                        rows, connection, staging, target, matchIndices, beforeImages, deleteMissing,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (useMerge)
            {
                return await MergeAsync(
                        rows, connection, staging, target, columnList, writeIndices, matchIndices,
                        readIndices, deleteMissing, cancellationToken)
                    .ConfigureAwait(false);
            }

            var conflictTarget = string.Join(
                ", ",
                matchIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

            // Match columns are what identify the row, so they are never themselves reassigned, and
            // an insert-only column is written by the insert arm alone.
            var updates = writeIndices
                .Where(i => !matchIndices.Contains(i) && !rows.Columns[i].IsInsertOnly)
                .Select(i =>
                {
                    var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                    return $"{column} = EXCLUDED.{column}";
                })
                .ToList();

            var conflictAction = updates.Count == 0
                ? "DO NOTHING"
                : $"DO UPDATE SET {string.Join(", ", updates)}";

            // RETURNING cannot see the staging table here, so generated values are correlated by
            // the match columns instead -- which are unique by definition, since ON CONFLICT
            // requires a unique index over them.
            var returning = new List<string>();
            foreach (var i in matchIndices.Concat(readIndices).Distinct())
            {
                returning.Add(_sqlHelper.DelimitIdentifier(rows.Columns[i].Name));
            }

            var approximate = rows.MergeCounts == MergeCounts.Approximate;

            if (approximate)
            {
                // xmax is zero on a freshly inserted tuple and non-zero on one that was updated.
                // A widely-used convention rather than a documented guarantee, so it only ever
                // splits the reported counts -- never decides what data gets written.
                returning.Add("(xmax = 0) AS __efbulk_inserted");
            }

            // Counted before the merge and inside the same transaction, so it reflects the rows
            // the merge is about to match. One indexed existence check over the staged values.
            var willUpdate = approximate
                ? 0
                : await CountExistingAsync(
                        connection, target, staging, rows, matchIndices, cancellationToken)
                    .ConfigureAwait(false);

            var sql = $"INSERT INTO {target} ({columnList}) SELECT {columnList} FROM {staging} "
                + $"ON CONFLICT ({conflictTarget}) {conflictAction} "
                + $"RETURNING {string.Join(", ", returning)}";

            var (inserted, updated) = await ApplyAsync(
                    rows, connection, sql, matchIndices, readIndices, approximate,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!approximate)
            {
                // ApplyAsync counted rows, not outcomes, when it had no xmax column to read.
                updated = willUpdate;
                inserted = rows.RowCount - willUpdate;
            }

            var deleted = 0;
            if (deleteMissing)
            {
                // ON CONFLICT has no delete arm, so removing what the source omitted is a separate
                // statement — run against the same staging table, inside the same transaction.
                var missing = string.Join(
                    " AND ",
                    matchIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        return $"t.{column} = s.{column}";
                    }));

                await using var delete = connection.CreateCommand();
                _settings.Apply(delete);

                // The scope fences which target rows the delete may reach at all, so it is ANDed
                // onto the "not in the source" test rather than replacing it.
                var scope = rows.Scope is { } fence ? $" AND ({fence.Sql})" : "";

                delete.CommandText =
                    $"DELETE FROM {target} AS t WHERE NOT EXISTS "
                    + $"(SELECT 1 FROM {staging} AS s WHERE {missing}){scope}";

                Bind(delete, rows.Scope);

                deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return (inserted, updated, deleted);
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

    /// <summary>
    ///     Whether this server can run <c>MERGE ... RETURNING merge_action()</c>.
    /// </summary>
    /// <remarks>
    ///     The version comes from the startup packet, so this costs nothing. It is only a
    ///     capability probe by proxy, which is why the setting can override it: a pooler or a
    ///     PostgreSQL-compatible engine can report 17 without implementing what 17 added.
    /// </remarks>
    /// <summary>
    ///     Reads the rows a merge or synchronise is about to change or remove, as they stand now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Two reads, answering two different questions. The first joins the staging table to
    ///         the target, so every source row learns whether the target already held it — which is
    ///         both the before-image and, for free, the per-row insert-versus-update split that a
    ///         merge otherwise only reports in total.
    ///     </para>
    ///     <para>
    ///         The second runs only for a synchronise, and finds the rows its delete arm is about to
    ///         remove: the ones the source does not contain, inside whatever scope confines it.
    ///         Those rows correspond to nothing the caller passed in, so they are the one set an
    ///         observer could not otherwise learn about at all.
    ///     </para>
    /// </remarks>
    private async Task CaptureBeforeAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
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

        // The staging table's columns carry the target's own names here, so the join predicate is
        // simply column-for-column on the match set.
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

            matched.CommandText =
                $"ANALYZE {staging}; "
                + $"SELECT s.{ordinal}, {projection} FROM {staging} AS s "
                + $"JOIN {target} AS t ON {match} FOR UPDATE OF t";

            await using var reader = await matched.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (reader.FieldCount == 0
                && await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
            }

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

        // The same predicate the delete arm uses, so the rows captured here are exactly the rows it
        // goes on to remove — scope included, since a scope is what holds it back.
        var scope = rows.Scope is { } fence ? $" AND ({fence.Sql})" : "";

        removed.CommandText =
            $"SELECT {projection} FROM {target} AS t WHERE NOT EXISTS "
            + $"(SELECT 1 FROM {staging} AS s WHERE {match}){scope} FOR UPDATE";

        Bind(removed, rows.Scope);

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

    private bool SupportsMerge(NpgsqlConnection connection)
        => _useMerge ?? connection.PostgreSqlVersion.Major >= 17;

    /// <summary>
    ///     Upserts through <c>MERGE</c>, available from PostgreSQL 17.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is strictly better than <c>ON CONFLICT</c> where it exists.
    ///         <c>merge_action()</c> states per row whether it was inserted, updated or deleted, so
    ///         the counts are exact without the extra pre-merge count the older path needs and
    ///         without reading <c>xmax</c>, which is a convention rather than a guarantee.
    ///         <c>RETURNING</c> may name the source, so generated values correlate by ordinal
    ///         rather than by match value. And <c>WHEN NOT MATCHED BY SOURCE</c> folds a
    ///         synchronise's delete into the same statement.
    ///     </para>
    ///     <para>
    ///         One behavioural difference is worth knowing. <c>MERGE</c> has none of
    ///         <c>ON CONFLICT</c>'s speculative-insertion locking, so under <c>READ COMMITTED</c> a
    ///         concurrent insert of the same key surfaces as a unique violation rather than being
    ///         absorbed into an update. It also raises an error when the source joins the same
    ///         target row twice, where <c>ON CONFLICT</c> would take the last write.
    ///     </para>
    /// </remarks>
    private async Task<(int Inserted, int Updated, int Deleted)> MergeAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        string staging,
        string target,
        string columnList,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
        bool deleteMissing,
        CancellationToken cancellationToken)
    {
        var match = new HashSet<int>(matchIndices);

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
            .Where(i => !match.Contains(i) && !rows.Columns[i].IsInsertOnly)
            .Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"{column} = s.{column}";
            })
            .ToList();

        var matched = assignments.Count == 0
            ? ""
            : $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", assignments)}\n";

        var values = string.Join(
            ", ",
            writeIndices.Select(i => $"s.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        // NOT MATCHED BY SOURCE sees only the target, which is exactly what the scope selects, so
        // the fence goes straight onto the arm's own condition.
        var fence = rows.Scope is { } scope ? $" AND ({scope.Sql})" : "";

        var notMatchedBySource = deleteMissing
            ? $"\nWHEN NOT MATCHED BY SOURCE{fence} THEN DELETE"
            : "";

        var returning = new List<string>
        {
            "merge_action()",
            $"s.{_sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName)}"
        };

        returning.AddRange(
            readIndices.Select(i => $"t.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        var sql =
            $"MERGE INTO {target} AS t\n"
            + $"USING {staging} AS s\n"
            + $"ON {on}\n"
            + matched
            + $"WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({values})"
            + notMatchedBySource
            + $"\nRETURNING {string.Join(", ", returning)}";

        var inserted = 0;
        var updated = 0;
        var deleted = 0;

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        Bind(command, deleteMissing ? rows.Scope : null);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var action = reader.GetString(0);

            if (string.Equals(action, "DELETE", StringComparison.Ordinal))
            {
                // A deleted row came from the target, so there is no source ordinal and nothing to
                // propagate back.
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

            var row = reader.GetInt32(1);

            for (var i = 0; i < readIndices.Count; i++)
            {
                var position = 2 + i;
                var value = await reader.IsDBNullAsync(position, cancellationToken)
                    .ConfigureAwait(false)
                    ? null
                    : reader.GetValue(position);

                rows.SetGeneratedValue(row, readIndices[i], value);
            }
        }

        return (inserted, updated, deleted);
    }

    private async Task<(int Inserted, int Updated)> ApplyAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
        bool approximate,
        CancellationToken cancellationToken)
    {
        // Rows are found by their match values, which the statement returns alongside whatever the
        // database generated.
        var byMatch = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var row = 0; row < rows.RowCount; row++)
        {
            byMatch[BulkRowMatching.KeyOf(rows, row, matchIndices)] = row;
        }

        var returnedMatch = new object?[matchIndices.Count];
        var inserted = 0;
        var updated = 0;

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < matchIndices.Count; i++)
            {
                returnedMatch[i] = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);
            }

            if (approximate)
            {
                if (reader.GetBoolean(reader.FieldCount - 1))
                {
                    inserted++;
                }
                else
                {
                    updated++;
                }
            }

            if (!byMatch.TryGetValue(BulkRowMatching.KeyOf(returnedMatch), out var row))
            {
                continue;
            }

            // Generated columns follow the match columns in the RETURNING list, except where a
            // column is both -- those were already emitted and are skipped.
            var offset = matchIndices.Count;
            foreach (var readIndex in readIndices)
            {
                if (matchIndices.Contains(readIndex))
                {
                    continue;
                }

                var value = await reader.IsDBNullAsync(offset, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(offset);

                rows.SetGeneratedValue(row, readIndex, value);
                offset++;
            }
        }

        return (inserted, updated);
    }

    /// <summary>Counts how many of the staged match values already exist in the target.</summary>
    private async Task<int> CountExistingAsync(
        NpgsqlConnection connection,
        string target,
        string staging,
        IBulkRowSet rows,
        IReadOnlyList<int> matchIndices,
        CancellationToken cancellationToken)
    {
        var predicate = string.Join(
            " AND ",
            matchIndices.Select(i =>
            {
                var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            }));

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText =
            $"SELECT count(*) FROM {target} AS t "
            + $"WHERE EXISTS (SELECT 1 FROM {staging} AS s WHERE {predicate})";

        var count = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(count, provider: null);
    }

    /// <summary>Binds a scope's values, if the statement was built with one.</summary>
    /// <remarks>
    ///     Bound rather than formatted into the SQL, which is what makes the interpolated-string
    ///     overload of <c>WithinScope</c> safe to hand a value from outside the application.
    /// </remarks>
    private static void Bind(NpgsqlCommand command, BulkScope? scope)
    {
        if (scope is null)
        {
            return;
        }

        for (var i = 0; i < scope.Parameters.Count; i++)
        {
            command.Parameters.AddWithValue(
                BulkScope.ParameterName(i), scope.Parameters[i] ?? DBNull.Value);
        }
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
