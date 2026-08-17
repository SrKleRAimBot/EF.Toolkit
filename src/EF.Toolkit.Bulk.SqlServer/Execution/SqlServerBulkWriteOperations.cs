using EFToolkit.Bulk.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.SqlServer.Execution;

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

    private readonly BulkExecutionSettings _settings;
    private readonly int _indexThreshold;

    public SqlServerBulkWriteOperations(
        ISqlGenerationHelper sqlHelper,
        BulkExecutionSettings settings,
        int indexThreshold,
        Func<SqlBulkCopy> createBulkCopy)
    {
        _sqlHelper = sqlHelper;
        _settings = settings;
        _indexThreshold = indexThreshold;
        _createBulkCopy = createBulkCopy;
    }

    public Task<int> UpdateAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> conditionIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForUpdate(rows, conditionIndices, writeIndices);

        return WithStagingAsync(
            rows, connection, transaction, staged, readIndices, conditionIndices,
            staging =>
            {
                var assignments = string.Join(
                    ", ",
                    writeIndices.Select(i =>
                    {
                        var column = _sqlHelper.DelimitIdentifier(rows.Columns[i].Name);
                        var source = _sqlHelper.DelimitIdentifier(StagedName(staged, i, false));
                        return $"t.{column} = s.{source}";
                    }));

                // OUTPUT precedes FROM in SQL Server's UPDATE ... FROM form, and may name a table
                // from that FROM clause -- which is what lets the source ordinal come back.
                return $"UPDATE t SET {assignments} "
                    + $"OUTPUT {Output(rows, readIndices, "inserted")} "
                    + $"FROM {target} AS t INNER JOIN {staging} AS s "
                    + $"ON {JoinPredicate(rows, conditionIndices, staged)};";
            },
            cancellationToken);
    }

    public Task<int> DeleteAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> conditionIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var staged = StagingColumn.ForDelete(rows, conditionIndices);

        return WithStagingAsync(
            rows, connection, transaction, staged, [], conditionIndices,
            staging =>
                $"DELETE t OUTPUT {Output(rows, [], "deleted")} "
                + $"FROM {target} AS t INNER JOIN {staging} AS s "
                + $"ON {JoinPredicate(rows, conditionIndices, staged)};",
            cancellationToken);
    }

    private async Task<int> WithStagingAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        List<StagingColumn> staged,
        IReadOnlyList<int> readIndices,
        IReadOnlyList<int> conditionIndices,
        Func<string, string> buildSql,
        CancellationToken cancellationToken)
    {
        var staging = _sqlHelper.DelimitIdentifier($"#efbulk_{Guid.NewGuid():N}");
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
                connection, transaction,
                $"SELECT TOP 0 {projection}, CAST(0 AS int) AS {ordinal} "
                + $"INTO {staging} FROM {target};",
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var bulkCopy = _createBulkCopy())
            {
                bulkCopy.DestinationTableName = staging;
                for (var i = 0; i < staged.Count; i++)
                {
                    bulkCopy.ColumnMappings.Add(i, staged[i].Name);
                }

                bulkCopy.ColumnMappings.Add(staged.Count, StagingColumn.OrdinalColumnName);

                await bulkCopy
                    .WriteToServerAsync(
                        new BulkRowSetDataReader(rows, staged, includeOrdinal: true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var tally = new BulkRowTally(rows.RowCount);

            // The index is built after the load, not before: SqlBulkCopy into an indexed table pays
            // per-row maintenance, whereas building it over a freshly loaded heap is a single sort.
            // It rides along with the statement that needs it so preparing the staging table costs
            // no extra round trip.
            var prelude = Prelude(rows, staging, staged, conditionIndices);

            if (rows.BeforeImages is { } beforeImages)
            {
                // The prelude moves onto this statement, which is now the first one to join the
                // staging table and so the first that needs it prepared.
                await CaptureBeforeAsync(
                        rows, connection, transaction, staging, staged, conditionIndices,
                        beforeImages, prelude, cancellationToken)
                    .ConfigureAwait(false);

                prelude = "";
            }

            await using (var command = connection.CreateCommand())
            {
                _settings.Apply(command);
                command.CommandText = prelude + buildSql(staging);

                command.Transaction = transaction;

                await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    // The ordinal leads the OUTPUT list, so the read columns sit at a fixed offset
                    // no matter how many of them there are.
                    var row = reader.GetInt32(0);
                    tally.Mark(row);

                    // Anything the database regenerated -- a concurrency token, a computed column
                    // -- comes back here so the entity ends up matching the row.
                    for (var i = 0; i < readIndices.Count; i++)
                    {
                        var position = 1 + i;
                        var column = rows.Columns[readIndices[i]];

                        var value = await BulkValueReader
                            .ReadAsync(reader, position, column, cancellationToken)
                            .ConfigureAwait(false);

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
                        connection, transaction, $"DROP TABLE IF EXISTS {staging};",
                        CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Reads the rows the statement is about to change, as they stand now.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         SQL Server could return the before-image from the write itself, through
    ///         <c>OUTPUT deleted.*</c>. It is read separately anyway, for two reasons: a delete's
    ///         row set knows only the columns that located the row, so <c>deleted.*</c> would have
    ///         to be described column by column against a shape the operation never modelled; and
    ///         doing it the same way on both engines means one behaviour to reason about rather
    ///         than two that are nearly alike.
    ///     </para>
    ///     <para>
    ///         <c>UPDLOCK, HOLDLOCK</c> takes the row locks the write is about to take anyway. A
    ///         concurrent writer could otherwise change a row between this read and the statement
    ///         that follows, leaving a captured image describing a state the write never replaced.
    ///     </para>
    /// </remarks>
    private async Task CaptureBeforeAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> conditionIndices,
        BulkBeforeImages beforeImages,
        string prelude,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var ordinal = _sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName);

        var projection = string.Join(
            ", ",
            beforeImages.Columns.Select(c => $"t.{_sqlHelper.DelimitIdentifier(c.Name)}"));

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.Transaction = transaction;

        command.CommandText = prelude
            + $"SELECT s.{ordinal}, {projection} FROM {staging} AS s "
            + $"INNER JOIN {target} AS t WITH (UPDLOCK, HOLDLOCK) "
            + $"ON {JoinPredicate(rows, conditionIndices, staged)};";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = reader.GetInt32(0);

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

    /// <summary>Builds the statements that precede the set-based statement.</summary>
    /// <remarks>
    ///     The index is clustered, which is the opposite of the usual advice and right here: the
    ///     join reads every staged row and needs every column, so a nonclustered index would force
    ///     a lookup back into the heap per row. A clustered index reorganises the heap in place and
    ///     carries the payload, and its statistics come with it.
    /// </remarks>
    private string Prelude(
        IBulkRowSet rows,
        string staging,
        IReadOnlyList<StagingColumn> staged,
        IReadOnlyList<int> conditionIndices)
    {
        if (!StagingPrelude.ShouldIndex(rows.RowCount, conditionIndices.Count, _indexThreshold))
        {
            return "";
        }

        var columns = string.Join(
            ", ",
            conditionIndices.Select(
                i => _sqlHelper.DelimitIdentifier(StagedName(staged, i, true))));

        var name = _sqlHelper.DelimitIdentifier("IX_efbulk_staging");

        // SET NOCOUNT ON so the DDL contributes no result set for the reader to step over.
        return $"SET NOCOUNT ON; CREATE CLUSTERED INDEX {name} ON {staging} ({columns}); ";
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
    ///     Builds the OUTPUT list: the source ordinal, then anything to read back.
    /// </summary>
    /// <remarks>
    ///     Key columns used to lead this list purely so returned rows could be matched to source
    ///     rows by their key values. The ordinal does that directly, so the keys — which the caller
    ///     already has — no longer cross the wire at all.
    /// </remarks>
    private string Output(IBulkRowSet rows, IReadOnlyList<int> readIndices, string source)
    {
        var parts = new List<string>(readIndices.Count + 1)
        {
            $"s.{_sqlHelper.DelimitIdentifier(StagingColumn.OrdinalColumnName)}"
        };

        parts.AddRange(
            readIndices.Select(
                i => $"{source}.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

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
        SqlConnection connection,
        SqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
