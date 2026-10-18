using System.Data.Common;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     The connection, transaction and settings one operation runs under, and the SQL helpers
///     shared by the executors that drive an engine through plain ADO.NET commands.
/// </summary>
/// <remarks>
///     The MySQL and SQLite executors work against <see cref="DbConnection" /> rather than a
///     particular driver. Neither needs anything a driver adds beyond ADO.NET, and not depending on
///     one is what keeps EF.Toolkit.Bulk.MySql from tying an application to a particular MySQL
///     provider.
/// </remarks>
internal class BulkCommandSession
{
    private readonly ISqlGenerationHelper _sqlHelper;

    public BulkCommandSession(
        ISqlGenerationHelper sqlHelper,
        DbConnection connection,
        DbTransaction? transaction,
        BulkExecutionSettings settings)
    {
        _sqlHelper = sqlHelper;
        Connection = connection;
        Transaction = transaction;
        Settings = settings;
    }

    public DbConnection Connection { get; }

    public DbTransaction? Transaction { get; }

    public BulkExecutionSettings Settings { get; }

    /// <summary>Delimits a column or unqualified table name.</summary>
    public string Quote(string name) => _sqlHelper.DelimitIdentifier(name);

    /// <summary>The row set's target table, delimited.</summary>
    public string Target(IBulkRowSet rows) => _sqlHelper.DelimitIdentifier(rows.TableName, rows.Schema);

    /// <summary>A comma-separated list of the named columns, delimited.</summary>
    public string ColumnList(IBulkRowSet rows, IEnumerable<int> indices, string? alias = null)
    {
        var prefix = alias is null ? "" : alias + ".";
        return string.Join(", ", indices.Select(i => prefix + Quote(rows.Columns[i].Name)));
    }

    /// <summary>
    ///     <c>t.column = s.column AND ...</c> over <paramref name="indices" />, for joining a staging
    ///     table whose columns carry the target's names.
    /// </summary>
    public string Join(IBulkRowSet rows, IEnumerable<int> indices)
        => string.Join(
            " AND ",
            indices.Select(i =>
            {
                var column = Quote(rows.Columns[i].Name);
                return $"t.{column} = s.{column}";
            }));

    /// <summary>Creates a command enlisted in the operation's transaction.</summary>
    public DbCommand CreateCommand(string sql)
    {
        var command = Connection.CreateCommand();
        Settings.Apply(command);
        command.Transaction = Transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Runs a statement that takes no row values, binding a scope if one is given.</summary>
    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        BulkScope? scope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        BulkParameters.BindScope(command, scope);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads every result row of <paramref name="reader" />, handing each field to
    ///     <paramref name="sink" /> as the model type of the matching entry in
    ///     <paramref name="columns" />.
    /// </summary>
    /// <param name="reader">The reader, positioned before its first row.</param>
    /// <param name="columns">What the fields from <paramref name="offset" /> onwards hold.</param>
    /// <param name="offset">The position of the first field to read.</param>
    /// <param name="sink">Receives the column index and value of each field.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>How many rows the reader held.</returns>
    public static async Task<int> ReadAllAsync(
        DbDataReader reader,
        IReadOnlyList<BulkColumnInfo> columns,
        int offset,
        Action<int, object?> sink,
        CancellationToken cancellationToken)
    {
        var count = 0;

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < columns.Count; i++)
            {
                sink(
                    i,
                    await BulkValueReader
                        .ReadAsync(reader, offset + i, columns[i], cancellationToken)
                        .ConfigureAwait(false));
            }

            count++;
        }

        return count;
    }

    /// <summary>Reads the rows a synchronise is about to remove into <paramref name="beforeImages" />.</summary>
    /// <param name="sql">
    ///     A <c>SELECT</c> of <see cref="BulkBeforeImages.Columns" />, in order, over exactly the rows
    ///     the delete will remove.
    /// </param>
    /// <param name="scope">The synchronise's scope, whose values the statement binds.</param>
    /// <param name="beforeImages">Where to record the rows.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task CaptureRemovedAsync(
        string sql,
        BulkScope? scope,
        BulkBeforeImages beforeImages,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(sql);
        BulkParameters.BindScope(command, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new object?[beforeImages.Columns.Count];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await BulkValueReader
                    .ReadAsync(reader, i, beforeImages.Columns[i], cancellationToken)
                    .ConfigureAwait(false);
            }

            beforeImages.AddRemovedRow(values);
        }
    }
}
