using System.Data.Common;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Sqlite.Execution;

/// <summary>
///     The connection, transaction and settings one SQLite operation runs under, plus the helpers
///     for statements prepared once and run per row.
/// </summary>
internal sealed class SqliteSession : BulkCommandSession
{
    public SqliteSession(
        ISqlGenerationHelper sqlHelper,
        DbConnection connection,
        DbTransaction? transaction,
        BulkExecutionSettings settings)
        : base(sqlHelper, connection, transaction, settings)
    {
    }

    /// <summary>
    ///     <c>column = @pN AND ...</c> over <paramref name="indices" />, numbering parameters from
    ///     <paramref name="firstParameter" />.
    /// </summary>
    public string Predicate(IBulkRowSet rows, IReadOnlyList<int> indices, int firstParameter)
        => string.Join(
            " AND ",
            indices.Select((index, i) =>
                $"{Quote(rows.Columns[index].Name)} = {SqliteRowStatement.Parameter(firstParameter + i)}"));

    /// <summary>The parameter list <c>@p0, @p1, ...</c> for a layout of <paramref name="count" /> values.</summary>
    public static string Parameters(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(SqliteRowStatement.Parameter));

    /// <summary>Prepares a statement to be run once per row.</summary>
    public SqliteRowStatement Statement(
        string sql,
        IBulkRowSet rows,
        IReadOnlyList<StagingColumn> layout)
        => new(Connection, Transaction, Settings, sql, rows, layout);
}
