using EFToolkit.Sql;
using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;
using EFToolkit.Sql.Execution;
using EFToolkit.Sql.Sessions;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Database objects, routine calls and session context at runtime.</summary>
/// <remarks>
///     <para>
///         Every value passed here reaches the database as a bound parameter through EF's own raw-SQL
///         pipeline — <c>ExecuteSqlRawAsync</c> and <c>SqlQueryRaw</c> — so the context's transaction,
///         interceptors and logging apply. Only names are rendered into the SQL text, and those are
///         quoted.
///     </para>
///     <para>
///         Retries differ by method. <c>CreateDatabaseObjectsAsync</c> and <c>DropDatabaseObjectsAsync</c>,
///         when they start their own transaction — no transaction open, and not MySQL — run the whole set
///         inside the context's execution strategy, so a retrying strategy retries it as one unit. Inside
///         a transaction of yours they run in it, and retrying is the caller's; on MySQL, which commits
///         each DDL statement as it runs, they are not retried, and a failure part way through can leave
///         a replaced object dropped and not yet recreated. <c>QueryProcedureAsync</c> and <c>QueryFunction</c> are queries, retried
///         as EF retries any query. <c>ExecuteProcedureAsync</c>, <c>SetSessionContextAsync</c> and
///         <c>RefreshMaterializedViewAsync</c> are not retried: EF does not retry raw commands on its
///         own, and a procedure may not be safe to run twice. Wrap them in
///         <c>Database.CreateExecutionStrategy().ExecuteAsync(...)</c> where it is.
///     </para>
/// </remarks>
public static class SqlDatabaseFacadeExtensions
{
    /// <summary>The SQL dialect of the context's provider.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <returns>The dialect.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not one EF.Toolkit.Sql knows.</exception>
    public static SqlDialect GetSqlDialect(this DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return SqlDialectDetector.FromProviderName(database.ProviderName);
    }

    /// <summary>Creates database objects, in order, on a database that does not run migrations.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="objects">The objects, dependencies first.</param>
    /// <returns>A task that completes when every object exists.</returns>
    /// <remarks>
    ///     For databases built with <c>EnsureCreated</c> — test databases above all — which never run
    ///     the migrations the objects would otherwise come from. Passing the same definitions the
    ///     migrations use keeps the two from drifting.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     await context.Database.EnsureCreatedAsync();
    ///     await context.Database.CreateDatabaseObjectsAsync(Schema.ActiveCustomers, Schema.TenantIsolation);
    ///     </code>
    /// </example>
    public static Task CreateDatabaseObjectsAsync(this DatabaseFacade database, params IDatabaseObject[] objects)
        => CreateDatabaseObjectsAsync(database, (IEnumerable<IDatabaseObject>)objects, CancellationToken.None);

    /// <summary>Creates database objects, in order, on a database that does not run migrations.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="objects">The objects, dependencies first.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when every object exists.</returns>
    public static async Task CreateDatabaseObjectsAsync(
        this DatabaseFacade database,
        IEnumerable<IDatabaseObject> objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(objects);

        var dialect = database.GetSqlDialect();
        var statements = objects.SelectMany(o => (o ?? throw new ArgumentException("Objects cannot contain null.", nameof(objects))).GetCreateSql(dialect)).ToList();

        await ExecuteAllAsync(database, statements, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops database objects, in reverse order, so dependents go before what they depend on.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="objects">The objects, in the order they were created.</param>
    /// <returns>A task that completes when every object is gone.</returns>
    public static Task DropDatabaseObjectsAsync(this DatabaseFacade database, params IDatabaseObject[] objects)
        => DropDatabaseObjectsAsync(database, (IEnumerable<IDatabaseObject>)objects, CancellationToken.None);

    /// <summary>Drops database objects, in reverse order, so dependents go before what they depend on.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="objects">The objects, in the order they were created.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when every object is gone.</returns>
    public static async Task DropDatabaseObjectsAsync(
        this DatabaseFacade database,
        IEnumerable<IDatabaseObject> objects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(objects);

        var dialect = database.GetSqlDialect();
        var statements = objects.Reverse().SelectMany(o => (o ?? throw new ArgumentException("Objects cannot contain null.", nameof(objects))).GetDropSql(dialect)).ToList();

        await ExecuteAllAsync(database, statements, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-runs a PostgreSQL materialized view's query and replaces its contents.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="name">The materialized view name.</param>
    /// <param name="concurrently">
    ///     Refresh without locking out readers. PostgreSQL requires a unique index on the view for this,
    ///     and refuses it on a view that has never been populated.
    /// </param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the refresh has.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is not PostgreSQL.</exception>
    public static async Task RefreshMaterializedViewAsync(
        this DatabaseFacade database,
        string name,
        bool concurrently = false,
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var sql = MaterializedViewDefinition.RefreshSql(database.GetSqlDialect(), name, schema, concurrently);
        await database.ExecuteSqlRawAsync(SqlSyntax.EscapeFormat(sql), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Calls a stored procedure.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="name">The procedure name.</param>
    /// <param name="args">
    ///     The arguments: an object whose properties name them (<c>new { tenantId = 1 }</c>), or an
    ///     <see cref="IReadOnlyDictionary{TKey, TValue}" />. A value that is a <c>DbParameter</c> is
    ///     passed as is, so an output parameter can be read after the call. PostgreSQL matches
    ///     arguments by name; MySQL by position, in the order given.
    /// </param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rows affected, as the provider reports them for the call.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static Task<int> ExecuteProcedureAsync(
        this DatabaseFacade database,
        string name,
        object? args = null,
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (sql, arguments) = RoutineCallSql.Procedure(database.GetSqlDialect(), name, schema, args);
        return database.ExecuteSqlRawAsync(sql, Parameters(arguments), cancellationToken);
    }

    /// <summary>Calls a stored procedure and reads the rows it returns.</summary>
    /// <typeparam name="T">
    ///     The row type: an unmapped class whose properties match the result's column names, or a
    ///     scalar, whose column must be named <c>Value</c>.
    /// </typeparam>
    /// <param name="database">The context's database facade.</param>
    /// <param name="name">The procedure name.</param>
    /// <param name="args">The arguments; see <see cref="ExecuteProcedureAsync" />.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The rows.</returns>
    /// <remarks>
    ///     Materialised rather than returned as a query, because a procedure call cannot be composed
    ///     over — SQL Server's <c>EXEC</c> cannot appear in a <c>FROM</c> clause, and neither can
    ///     <c>CALL</c> — so a <c>Where</c> appended to it would fail at execution. On PostgreSQL the
    ///     rows are the single row of <c>OUT</c> and <c>INOUT</c> parameters that <c>CALL</c> returns;
    ///     on SQL Server and MySQL they are the procedure's first result set.
    /// </remarks>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static Task<List<T>> QueryProcedureAsync<T>(
        this DatabaseFacade database,
        string name,
        object? args = null,
        string? schema = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (sql, arguments) = RoutineCallSql.Procedure(database.GetSqlDialect(), name, schema, args);
        return database.SqlQueryRaw<T>(sql, Parameters(arguments)).ToListAsync(cancellationToken);
    }

    /// <summary>Queries a table-valued function as a composable query.</summary>
    /// <typeparam name="T">
    ///     The row type: an unmapped class whose properties match the function's columns, or a scalar
    ///     whose column is named <c>Value</c>.
    /// </typeparam>
    /// <param name="database">The context's database facade.</param>
    /// <param name="name">The function name.</param>
    /// <param name="args">
    ///     The arguments; see <see cref="ExecuteProcedureAsync" />. PostgreSQL matches them by name, SQL
    ///     Server by position.
    /// </param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <returns>
    ///     A query over <c>SELECT * FROM function(...)</c>. LINQ composed onto it — <c>Where</c>,
    ///     <c>OrderBy</c>, <c>Take</c> — runs in the database.
    /// </returns>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is MySQL or SQLite.</exception>
    public static IQueryable<T> QueryFunction<T>(
        this DatabaseFacade database,
        string name,
        object? args = null,
        string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (sql, arguments) = RoutineCallSql.Function(database.GetSqlDialect(), name, schema, args);
        return database.SqlQueryRaw<T>(sql, Parameters(arguments));
    }

    /// <summary>Sets a session-context value on the context's connection.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="key">
    ///     The key. On PostgreSQL a dotted custom setting name such as <c>app.tenant_id</c>; on MySQL a
    ///     user variable name, without <c>@</c>.
    /// </param>
    /// <param name="value">The value; <see langword="null" /> clears it.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the value is set.</returns>
    /// <remarks>
    ///     <para>
    ///         Opens the context's connection and leaves it open, because the value belongs to the
    ///         database session: had EF closed the connection after this command, as it does by default,
    ///         the pool would reset the session and the next query would run without the value. The
    ///         connection closes with the context, or with <c>CloseConnection</c>.
    ///     </para>
    ///     <para>
    ///         For a value every connection needs — a tenant for row-level security — prefer
    ///         <c>UseSessionContext</c> on the options, which cannot be forgotten.
    ///     </para>
    ///     <para>
    ///         MySQL connectors treat <c>@name</c> as a parameter unless user variables are allowed:
    ///         add <c>AllowUserVariables=True</c> (MySqlConnector, under Pomelo) or
    ///         <c>Allow User Variables=True</c> (Oracle's provider) to the connection string.
    ///     </para>
    /// </remarks>
    /// <exception cref="SqlFeatureNotSupportedException">The provider is SQLite.</exception>
    public static async Task SetSessionContextAsync(
        this DatabaseFacade database,
        string key,
        object? value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (sql, arguments) = SessionContextSql.ForRawSql(database.GetSqlDialect(), key, value);

        // EF counts every OpenConnection and closes only when each is matched, so opening again on an
        // already-open connection would make the one CloseConnection the remarks promise insufficient.
        // The setter opens only a closed connection — once, however many values are set — and undoes
        // that open if the command fails.
        var opened = database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await database.ExecuteSqlRawAsync(sql, Parameters(arguments), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (opened)
            {
                await database.CloseConnectionAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    // EF maps a null argument to a typed null parameter itself; DBNull has no type mapping and is
    // refused, so nulls are passed through as they are.
    private static object[] Parameters(object?[] arguments) => arguments!;

    // One transaction for the whole set, unless the caller already has one, which the statements join.
    // Replacing a definition is a drop followed by a create; run as separate commands, the moment
    // between them is visible to every other session — for tenant isolation, a moment in which the
    // restrictive policy is gone and the permissive grant is not, so any session reads every tenant —
    // and a create that fails leaves it that way. PostgreSQL, SQL Server and SQLite run DDL inside a
    // transaction; MySQL commits each DDL statement implicitly, so there it stays statement by statement.
    // Inside the execution strategy, so a retrying strategy retries the whole unit rather than refusing
    // a transaction it cannot replay.
    private static async Task ExecuteAllAsync(
        DatabaseFacade database,
        List<string> statements,
        CancellationToken cancellationToken)
    {
        if (database.GetSqlDialect() == SqlDialect.MySql)
        {
            // MySQL commits implicitly before every DDL statement. Inside the caller's transaction that
            // would commit the caller's earlier work behind their back — and take any savepoint with it —
            // so it is refused before anything runs. Outside one there is no unit to offer: the
            // statements run one by one, each committing as MySQL always does.
            if (InAnyTransaction(database))
            {
                throw new InvalidOperationException(
                    "MySQL commits implicitly before every DDL statement, so creating or dropping database objects "
                    + "inside a transaction would commit the work already done in it. Call this outside the "
                    + "transaction.");
            }

            await ExecuteEachAsync(database, statements, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (database.CurrentTransaction is { SupportsSavepoints: true } current)
        {
            // Inside the caller's transaction, a savepoint gives the set the same all-or-nothing: some
            // engines leave the transaction committable after a failed statement, so without it a caller
            // that caught the error and committed would commit the drop without the create.
            // A name of its own each time: SQL Server cannot release a savepoint, so one named like the
            // caller's would stay behind and take the caller's rollback to that name for itself.
            var savepoint = "ef_toolkit_" + Guid.NewGuid().ToString("N")[..20];
            await current.CreateSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteEachAsync(database, statements, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await current.RollbackToSavepointAsync(savepoint, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            await current.ReleaseSavepointAsync(savepoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (InAnyTransaction(database))
        {
            // A transaction without savepoints, or an ambient one: the set joins it, and its atomicity
            // is the caller's to decide.
            await ExecuteEachAsync(database, statements, cancellationToken).ConfigureAwait(false);
            return;
        }

        await database.CreateExecutionStrategy().ExecuteAsync(
                (database, statements),
                static async (_, state, token) =>
                {
                    var transaction = await state.database.BeginTransactionAsync(token).ConfigureAwait(false);
                    await using (transaction.ConfigureAwait(false))
                    {
                        await ExecuteEachAsync(state.database, state.statements, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }

                    return true;
                },
                verifySucceeded: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // EF's own transaction, an ambient TransactionScope, or one enlisted explicitly with
    // Database.EnlistTransaction — which neither of the other two shows.
    private static bool InAnyTransaction(DatabaseFacade database)
        => database.CurrentTransaction is not null
            || System.Transactions.Transaction.Current is not null
            || database.GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalConnection>()
                is Microsoft.EntityFrameworkCore.Storage.RelationalConnection { EnlistedTransaction: not null };

    private static async Task ExecuteEachAsync(
        DatabaseFacade database,
        List<string> statements,
        CancellationToken cancellationToken)
    {
        foreach (var statement in statements)
        {
            // DDL has no parameters, but EF formats every raw statement, so braces in a body (a JSON
            // literal, a SQL Server ODBC escape) must be escaped to arrive intact.
            await database.ExecuteSqlRawAsync(SqlSyntax.EscapeFormat(statement), cancellationToken).ConfigureAwait(false);
        }
    }
}
