using EFBulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EFBulk.PostgreSQL.Execution;

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
    private readonly Func<NpgsqlConnection, string, IReadOnlyList<int>, IBulkRowSet, CancellationToken, Task> _copyInto;

    public NpgsqlBulkMerge(
        ISqlGenerationHelper sqlHelper,
        Func<NpgsqlConnection, string, IReadOnlyList<int>, IBulkRowSet, CancellationToken, Task> copyInto)
    {
        _sqlHelper = sqlHelper;
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

        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        await ExecuteNonQueryAsync(
                connection,
                $"CREATE TEMP TABLE {staging} AS SELECT {columnList} FROM {target} WITH NO DATA",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await _copyInto(connection, staging, writeIndices, rows, cancellationToken)
                .ConfigureAwait(false);

            var conflictTarget = string.Join(
                ", ",
                matchIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

            // Match columns are what identify the row, so they are never themselves reassigned.
            var updates = writeIndices
                .Where(i => !matchIndices.Contains(i))
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

            // xmax is zero on a freshly inserted tuple and non-zero on one that was updated. This
            // is a well-known convention rather than a documented guarantee, so it is used only to
            // split the reported counts -- never to decide what data gets written.
            returning.Add("(xmax = 0) AS __efbulk_inserted");

            var sql = $"INSERT INTO {target} ({columnList}) SELECT {columnList} FROM {staging} "
                + $"ON CONFLICT ({conflictTarget}) {conflictAction} "
                + $"RETURNING {string.Join(", ", returning)}";

            var (inserted, updated) = await ApplyAsync(
                    rows, connection, sql, matchIndices, readIndices, cancellationToken)
                .ConfigureAwait(false);

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
                delete.CommandText =
                    $"DELETE FROM {target} AS t WHERE NOT EXISTS "
                    + $"(SELECT 1 FROM {staging} AS s WHERE {missing})";

                deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return (inserted, updated, deleted);
        }
        finally
        {
            await ExecuteNonQueryAsync(
                    connection, $"DROP TABLE IF EXISTS {staging}", CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task<(int Inserted, int Updated)> ApplyAsync(
        IBulkRowSet rows,
        NpgsqlConnection connection,
        string sql,
        IReadOnlyList<int> matchIndices,
        IReadOnlyList<int> readIndices,
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

            var wasInserted = reader.GetBoolean(reader.FieldCount - 1);
            if (wasInserted)
            {
                inserted++;
            }
            else
            {
                updated++;
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
