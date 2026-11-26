using System.Data.Common;
using EFToolkit.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Empties a test database between tests.</summary>
/// <remarks>
///     The model-driven counterpart to Respawn: the tables, and the order they can be emptied in,
///     come from the context's model rather than the database's catalog.
/// </remarks>
public static class DatabaseResetExtensions
{
    /// <summary>
    ///     Deletes every row of every table the model maps, in foreign-key-safe order, restarts their
    ///     identity counters, and clears the context's change tracker.
    /// </summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="configure">Tables to leave alone, and whether identities restart.</param>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>A task that completes once the tables are empty.</returns>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL, SQL Server, MySQL or SQLite.</exception>
    /// <remarks>
    ///     Joins the context's current transaction if it has one, so a reset inside an isolated scope
    ///     is rolled back with it. On MySQL that costs the identity restart — see the docs.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     await context.Database.ResetDataAsync();
    ///     await context.Database.ResetDataAsync(o => o.ExcludeEntity&lt;Country&gt;());
    ///     </code>
    /// </example>
    public static async Task ResetDataAsync(
        this DatabaseFacade database,
        Action<ResetDataOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (context, engine, plan, options) = Prepare(database, configure);

        if (plan.Tables.Count > 0)
        {
            await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var connection = database.GetDbConnection();
                var transaction = database.CurrentTransaction?.GetDbTransaction();
                var state = await ReadStateAsync(engine, connection, transaction, options, cancellationToken)
                    .ConfigureAwait(false);
                var script = ResetSqlGenerator.Generate(engine, plan, state);

                foreach (var sql in script.Prologue)
                {
                    await ExecuteAsync(connection, transaction, database, sql, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    foreach (var sql in script.Body)
                    {
                        await ExecuteAsync(connection, transaction, database, sql, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    foreach (var sql in script.Epilogue)
                    {
                        // Not cancellable: abandoning the restore is the one outcome worse than waiting.
                        await ExecuteAsync(connection, transaction, database, sql, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        context.ChangeTracker.Clear();
    }

    /// <summary>
    ///     Deletes every row of every table the model maps, in foreign-key-safe order, restarts their
    ///     identity counters, and clears the context's change tracker.
    /// </summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="configure">Tables to leave alone, and whether identities restart.</param>
    /// <exception cref="NotSupportedException">The provider is not PostgreSQL, SQL Server, MySQL or SQLite.</exception>
    public static void ResetData(this DatabaseFacade database, Action<ResetDataOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(database);

        var (context, engine, plan, options) = Prepare(database, configure);

        if (plan.Tables.Count > 0)
        {
            database.OpenConnection();
            try
            {
                var connection = database.GetDbConnection();
                var transaction = database.CurrentTransaction?.GetDbTransaction();
                var state = ReadState(engine, connection, transaction, options);
                var script = ResetSqlGenerator.Generate(engine, plan, state);

                foreach (var sql in script.Prologue)
                {
                    Execute(connection, transaction, database, sql);
                }

                try
                {
                    foreach (var sql in script.Body)
                    {
                        Execute(connection, transaction, database, sql);
                    }
                }
                finally
                {
                    foreach (var sql in script.Epilogue)
                    {
                        Execute(connection, transaction, database, sql);
                    }
                }
            }
            finally
            {
                database.CloseConnection();
            }
        }

        context.ChangeTracker.Clear();
    }

    /// <summary>The SQL a reset of this context's database would run, without running it.</summary>
    /// <remarks>
    ///     For tests of the generated SQL. Assumes foreign keys are enforced and, on SQLite, that
    ///     <c>sqlite_sequence</c> exists, since answering either needs the database.
    /// </remarks>
    internal static ResetScript GenerateResetScript(
        DatabaseFacade database,
        Action<ResetDataOptions>? configure = null,
        bool inTransaction = false)
    {
        var (_, engine, plan, options) = Prepare(database, configure);
        return ResetSqlGenerator.Generate(
            engine,
            plan,
            new ResetConnectionState(options.RestartIdentity, inTransaction, true, true));
    }

    internal static DbContext GetContext(this DatabaseFacade database)
        => ((IDatabaseFacadeDependenciesAccessor)database).Context;

    private static (DbContext Context, DatabaseEngine Engine, ResetPlan Plan, ResetDataOptions Options) Prepare(
        DatabaseFacade database,
        Action<ResetDataOptions>? configure)
    {
        var context = database.GetContext();
        var engine = DatabaseEngines.Detect(database.ProviderName);
        var options = ResetDataOptions.Build(configure);
        var plan = ResetPlan.Build(context.Model, options);
        return (context, engine, plan, options);
    }

    // An ambient TransactionScope counts too: the connection enlists in it, and a TRUNCATE on MySQL
    // would commit it just the same.
    private static bool InTransaction(DbTransaction? transaction)
        => transaction is not null || System.Transactions.Transaction.Current is not null;

    private static async Task<ResetConnectionState> ReadStateAsync(
        DatabaseEngine engine,
        DbConnection connection,
        DbTransaction? transaction,
        ResetDataOptions options,
        CancellationToken cancellationToken)
    {
        var inTransaction = InTransaction(transaction);
        if (engine == DatabaseEngine.MySql)
        {
            var checks = Convert.ToInt64(
                await ScalarAsync(connection, transaction, "SELECT @@FOREIGN_KEY_CHECKS;", cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            return new ResetConnectionState(options.RestartIdentity, inTransaction, MySqlForeignKeyChecks: checks);
        }

        if (engine != DatabaseEngine.Sqlite)
        {
            return new ResetConnectionState(options.RestartIdentity, inTransaction);
        }

        var foreignKeys = Convert.ToInt64(
            await ScalarAsync(connection, transaction, "PRAGMA foreign_keys;", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
        var sequence = Convert.ToInt64(
            await ScalarAsync(connection, transaction, SqliteSequenceExists, cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;

        return new ResetConnectionState(options.RestartIdentity, inTransaction, foreignKeys, sequence);
    }

    private static ResetConnectionState ReadState(
        DatabaseEngine engine,
        DbConnection connection,
        DbTransaction? transaction,
        ResetDataOptions options)
    {
        var inTransaction = InTransaction(transaction);
        if (engine == DatabaseEngine.MySql)
        {
            var checks = Convert.ToInt64(
                Scalar(connection, transaction, "SELECT @@FOREIGN_KEY_CHECKS;"),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            return new ResetConnectionState(options.RestartIdentity, inTransaction, MySqlForeignKeyChecks: checks);
        }

        if (engine != DatabaseEngine.Sqlite)
        {
            return new ResetConnectionState(options.RestartIdentity, inTransaction);
        }

        var foreignKeys = Convert.ToInt64(
            Scalar(connection, transaction, "PRAGMA foreign_keys;"),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
        var sequence = Convert.ToInt64(
            Scalar(connection, transaction, SqliteSequenceExists),
            System.Globalization.CultureInfo.InvariantCulture) != 0;

        return new ResetConnectionState(options.RestartIdentity, inTransaction, foreignKeys, sequence);
    }

    private const string SqliteSequenceExists =
        "SELECT COUNT(*) FROM \"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'sqlite_sequence';";

    // Straight to the ADO.NET connection rather than through ExecuteSqlRaw. The SQL carries braces
    // and identifiers that must not be read as format placeholders, and a reset is housekeeping:
    // it should not appear in an SQL capture that is asserting on the test's own commands.
    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        int? timeout = null)
    {
        var command = connection.CreateCommand();
#pragma warning disable CA2100 // Identifiers come from the model and are quoted; nothing is user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = transaction;
        if (timeout is { } seconds)
        {
            command.CommandTimeout = seconds;
        }

        return command;
    }

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        DatabaseFacade database,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = CreateCommand(connection, transaction, sql, database.GetCommandTimeout());
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Execute(DbConnection connection, DbTransaction? transaction, DatabaseFacade database, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql, database.GetCommandTimeout());
        command.ExecuteNonQuery();
    }

    private static async Task<object?> ScalarAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var command = CreateCommand(connection, transaction, sql);
        await using (command.ConfigureAwait(false))
        {
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static object? Scalar(DbConnection connection, DbTransaction? transaction, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql);
        return command.ExecuteScalar();
    }
}
