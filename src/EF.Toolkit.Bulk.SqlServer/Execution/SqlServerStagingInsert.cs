using EFToolkit.Bulk.Execution;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.SqlServer.Execution;

/// <summary>
///     Inserts rows that have store-generated values, by bulk-copying into a temporary table and
///     then merging into the target with an <c>OUTPUT</c> clause that correlates generated values
///     back to the source rows.
/// </summary>
/// <remarks>
///     <para>
///         SQL Server's <c>MERGE</c> is the right tool here specifically because its <c>OUTPUT</c>
///         clause may reference the <em>source</em> as well as <c>inserted.*</c>. A plain
///         <c>INSERT ... SELECT ... OUTPUT</c> cannot, which would leave generated keys arriving in
///         an order the engine never promised — the same problem that makes this approach unusable
///         on PostgreSQL before version 17.
///     </para>
///     <para>
///         Temporary tables are session-scoped, so every statement here runs on the same connection
///         the bulk copy used, inside EF's ambient transaction.
///     </para>
/// </remarks>
internal sealed class SqlServerStagingInsert
{
    /// <summary>Name of the synthetic column carrying each row's position.</summary>
    public const string OrdinalColumnName = "__efbulk_ord";

    private readonly ISqlGenerationHelper _sqlHelper;

    private readonly BulkExecutionSettings _settings;

    public SqlServerStagingInsert(ISqlGenerationHelper sqlHelper, BulkExecutionSettings settings)
    {
        _sqlHelper = sqlHelper;
        _settings = settings;
    }

    public async Task<int> ExecuteAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> readIndices,
        Func<SqlBulkCopy> createBulkCopy,
        CancellationToken cancellationToken)
    {
        var staging = $"#efbulk_{Guid.NewGuid():N}";
        var stagingRef = _sqlHelper.DelimitIdentifier(staging);

        await ExecuteNonQueryAsync(
                connection, transaction, CreateStagingSql(rows, stagingRef, writeIndices),
                cancellationToken)
            .ConfigureAwait(false);

        try
        {
            using (var bulkCopy = createBulkCopy())
            {
                bulkCopy.DestinationTableName = stagingRef;

                for (var i = 0; i < writeIndices.Count; i++)
                {
                    bulkCopy.ColumnMappings.Add(i, rows.Columns[writeIndices[i]].Name);
                }

                bulkCopy.ColumnMappings.Add(writeIndices.Count, OrdinalColumnName);

                await bulkCopy
                    .WriteToServerAsync(
                        new BulkRowSetDataReader(
                            rows, StagingColumn.ForWrite(rows, writeIndices), includeOrdinal: true),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await MergeAndPropagateAsync(
                    rows, connection, transaction, stagingRef, writeIndices, readIndices,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await StagingCleanup.RunAsync(
                    stagingRef,
                    () => ExecuteNonQueryAsync(
                        connection, transaction, $"DROP TABLE IF EXISTS {stagingRef};",
                        CancellationToken.None))
                .ConfigureAwait(false);
        }
    }

    /// <summary>Builds the staging table from the target's own definition.</summary>
    /// <remarks>
    ///     <c>SELECT TOP 0 ... INTO</c> copies the source column types without copying rows, which
    ///     avoids translating EF's store types into DDL and getting a facet wrong. Only written
    ///     columns are copied, so nothing has to suppress identity behaviour on the staging side.
    /// </remarks>
    private string CreateStagingSql(
        IBulkRowSet rows,
        string stagingRef,
        IReadOnlyList<int> writeIndices)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        var ordinal = _sqlHelper.DelimitIdentifier(OrdinalColumnName);

        return $"SELECT TOP 0 {columnList}, CAST(0 AS int) AS {ordinal} "
            + $"INTO {stagingRef} FROM {target};";
    }

    private async Task<int> MergeAndPropagateAsync(
        IBulkRowSet rows,
        SqlConnection connection,
        SqlTransaction? transaction,
        string stagingRef,
        IReadOnlyList<int> writeIndices,
        IReadOnlyList<int> readIndices,
        CancellationToken cancellationToken)
    {
        var target = _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);
        var columnList = string.Join(
            ", ",
            writeIndices.Select(i => _sqlHelper.DelimitIdentifier(rows.Columns[i].Name)));

        var sourceList = string.Join(
            ", ",
            writeIndices.Select(i => $"s.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        var outputList = string.Join(
            ", ",
            readIndices.Select(i => $"inserted.{_sqlHelper.DelimitIdentifier(rows.Columns[i].Name)}"));

        var ordinal = _sqlHelper.DelimitIdentifier(OrdinalColumnName);

        // ON 1 = 0 never matches, so every staged row takes the NOT MATCHED branch and is inserted.
        //
        // Deliberately no HOLDLOCK, unlike the upsert in SqlServerBulkMerge. That hint exists to
        // close the window between MERGE's match test and its insert, and there is no such window
        // here: the join never matches, so the statement only ever inserts. Taking a serializable
        // range lock would cost concurrency for a race that cannot occur.
        var sql = $"""
            MERGE INTO {target} AS t
            USING {stagingRef} AS s
            ON 1 = 0
            WHEN NOT MATCHED THEN INSERT ({columnList}) VALUES ({sourceList})
            OUTPUT {outputList}, s.{ordinal};
            """;

        await using var command = connection.CreateCommand();
        _settings.Apply(command);
        command.CommandText = sql;
        command.Transaction = transaction;

        var affected = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = reader.GetInt32(readIndices.Count);

            for (var i = 0; i < readIndices.Count; i++)
            {
                var value = await reader.IsDBNullAsync(i, cancellationToken).ConfigureAwait(false)
                    ? null
                    : reader.GetValue(i);

                rows.SetGeneratedValue(row, readIndices[i], value);
            }

            affected++;
        }

        return affected;
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
