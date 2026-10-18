using System.Data.Common;
using EFToolkit.Bulk.Execution;

namespace EFToolkit.Bulk.Sqlite.Execution;

/// <summary>
///     One parameterised statement, prepared once and executed for each row in turn.
/// </summary>
/// <remarks>
///     <para>
///         SQLite runs in the application's own process, so there is no round trip for a bulk
///         protocol to save, and a statement executed once per row is not the cost it is on a
///         networked engine. What does cost is everything EF Core does around each row: build a
///         command, generate its SQL, create its parameters, and propagate its results through the
///         change tracker. A prepared statement reused for the whole set skips all of it — the SQL
///         is compiled once, and each row is a bind and a step.
///     </para>
///     <para>
///         Microsoft.Data.Sqlite keeps a command's compiled statement for as long as its text is
///         unchanged, so reusing the command is all preparing it takes. Parameters are recreated per
///         row through the column's type mapping, which is what keeps every value bound exactly as
///         <c>SaveChanges</c> would bind it.
///     </para>
/// </remarks>
internal sealed class SqliteRowStatement : IAsyncDisposable
{
    private readonly DbCommand _command;
    private readonly IBulkRowSet _rows;
    private readonly IReadOnlyList<StagingColumn> _layout;

    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">EF's ambient transaction, if any.</param>
    /// <param name="settings">The resolved per-operation settings.</param>
    /// <param name="sql">The statement, referring to the layout's values as <see cref="Parameter" />.</param>
    /// <param name="rows">The rows whose values are bound.</param>
    /// <param name="layout">Which value each parameter carries, in parameter order.</param>
    public SqliteRowStatement(
        DbConnection connection,
        DbTransaction? transaction,
        BulkExecutionSettings settings,
        string sql,
        IBulkRowSet rows,
        IReadOnlyList<StagingColumn> layout)
    {
        _rows = rows;
        _layout = layout;
        _command = connection.CreateCommand();
        settings.Apply(_command);
        _command.Transaction = transaction;
        _command.CommandText = sql;
    }

    /// <summary>The name of the parameter carrying layout position <paramref name="index" />.</summary>
    public static string Parameter(int index) => "@p" + index;

    /// <summary>Binds <paramref name="row" /> and runs the statement for its affected-row count.</summary>
    /// <remarks>
    ///     Synchronous underneath, deliberately. Microsoft.Data.Sqlite's asynchronous methods run
    ///     the same in-process work and wrap the answer in a task, which per row is pure overhead —
    ///     there is no I/O wait for an await to release a thread during.
    /// </remarks>
    public Task<int> ExecuteNonQueryAsync(int row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Bind(row).ExecuteNonQuery());
    }

    /// <summary>Binds <paramref name="row" /> and runs the statement for its result rows.</summary>
    /// <remarks>Synchronous underneath, for the reason <see cref="ExecuteNonQueryAsync" /> gives.</remarks>
    public Task<DbDataReader> ExecuteReaderAsync(int row, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Bind(row).ExecuteReader());
    }

    public ValueTask DisposeAsync() => _command.DisposeAsync();

    private DbCommand Bind(int row)
    {
        _command.Parameters.Clear();

        for (var i = 0; i < _layout.Count; i++)
        {
            var staged = _layout[i];

            BulkParameters.Add(
                _command, Parameter(i), _rows.Columns[staged.Index], staged.ModelValueFor(_rows, row));
        }

        return _command;
    }
}
