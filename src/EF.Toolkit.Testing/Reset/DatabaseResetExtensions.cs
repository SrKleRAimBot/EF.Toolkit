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
                if (engine == DatabaseEngine.SqlServer)
                {
                    plan = plan.WithResolvedSchemas(
                        await ReadReferencesAsync(connection, transaction, DatabaseForeignKeys.ResolveSchemas(plan), cancellationToken)
                            .ConfigureAwait(false));
                }

                var state = await ReadStateAsync(engine, connection, transaction, options, cancellationToken)
                    .ConfigureAwait(false);
                if (DatabaseForeignKeys.Query(engine, plan, state.MySqlNoBackslashEscapes) is { } query)
                {
                    ResetPlan.RefuseUnplannedReferences(
                        await ReadReferencesAsync(connection, transaction, query, cancellationToken).ConfigureAwait(false));
                }

                if (engine == DatabaseEngine.SqlServer)
                {
                    var canSee = Convert.ToInt64(
                        await ScalarAsync(connection, transaction, DatabaseForeignKeys.SqlServerCanSeePolicies, cancellationToken).ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture) == 1;
                    ResetPlan.RefuseFilteredTables(
                        await ReadReferencesAsync(connection, transaction, DatabaseForeignKeys.SqlServerFilteredTables(plan), cancellationToken)
                            .ConfigureAwait(false),
                        canSee);
                }

                if (engine == DatabaseEngine.PostgreSql)
                {
                    ResetPlan.RefuseTruncatedDescendants(await GuardedAsync(
                            connection,
                            transaction,
                            state.InTransaction,
                            token => ReadReferencesAsync(connection, transaction, DatabaseForeignKeys.PostgreSqlDescendants(plan), token),
                            cancellationToken)
                        .ConfigureAwait(false));
                }

                if (engine == DatabaseEngine.MySql && ResetSqlGenerator.MySqlVerifies(plan, state))
                {
                    state = await WithMySqlForeignKeysAsync(connection, transaction, plan, state, cancellationToken).ConfigureAwait(false);
                }

                var script = ResetSqlGenerator.Generate(engine, plan, state);

                await ResetRunner.RunAsync(
                        script,
                        (sql, token) => ExecuteAsync(connection, transaction, database, sql, token),
                        async (sql, token) => Convert.ToInt64(
                            await ScalarAsync(connection, transaction, sql, token).ConfigureAwait(false),
                            System.Globalization.CultureInfo.InvariantCulture),
                        cancellationToken)
                    .ConfigureAwait(false);
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
                if (engine == DatabaseEngine.SqlServer)
                {
                    plan = plan.WithResolvedSchemas(
                        ReadReferences(connection, transaction, DatabaseForeignKeys.ResolveSchemas(plan)));
                }

                var state = ReadState(engine, connection, transaction, options);
                if (DatabaseForeignKeys.Query(engine, plan, state.MySqlNoBackslashEscapes) is { } query)
                {
                    ResetPlan.RefuseUnplannedReferences(ReadReferences(connection, transaction, query));
                }

                if (engine == DatabaseEngine.SqlServer)
                {
                    var canSee = Convert.ToInt64(
                        Scalar(connection, transaction, DatabaseForeignKeys.SqlServerCanSeePolicies),
                        System.Globalization.CultureInfo.InvariantCulture) == 1;
                    ResetPlan.RefuseFilteredTables(
                        ReadReferences(connection, transaction, DatabaseForeignKeys.SqlServerFilteredTables(plan)), canSee);
                }

                if (engine == DatabaseEngine.PostgreSql)
                {
                    ResetPlan.RefuseTruncatedDescendants(Guarded(
                        connection,
                        transaction,
                        state.InTransaction,
                        () => ReadReferences(connection, transaction, DatabaseForeignKeys.PostgreSqlDescendants(plan))));
                }

                if (engine == DatabaseEngine.MySql && ResetSqlGenerator.MySqlVerifies(plan, state))
                {
                    state = WithMySqlForeignKeys(connection, transaction, plan, state);
                }

                var script = ResetSqlGenerator.Generate(engine, plan, state);

                ResetRunner.Run(
                    script,
                    sql => Execute(connection, transaction, database, sql),
                    sql => Convert.ToInt64(Scalar(connection, transaction, sql), System.Globalization.CultureInfo.InvariantCulture));
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
            var noBackslashEscapes = Convert.ToInt64(
                await ScalarAsync(connection, transaction, MySqlNoBackslashEscapes, cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            return new ResetConnectionState(
                options.RestartIdentity, inTransaction, MySqlForeignKeyChecks: checks, MySqlNoBackslashEscapes: noBackslashEscapes);
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
        var deferred = Convert.ToInt64(
            await ScalarAsync(connection, transaction, "PRAGMA defer_foreign_keys;", cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;

        return new ResetConnectionState(
            options.RestartIdentity, inTransaction, foreignKeys, sequence, SqliteDeferForeignKeys: deferred);
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
            var noBackslashEscapes = Convert.ToInt64(
                Scalar(connection, transaction, MySqlNoBackslashEscapes),
                System.Globalization.CultureInfo.InvariantCulture) != 0;
            return new ResetConnectionState(
                options.RestartIdentity, inTransaction, MySqlForeignKeyChecks: checks, MySqlNoBackslashEscapes: noBackslashEscapes);
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
        var deferred = Convert.ToInt64(
            Scalar(connection, transaction, "PRAGMA defer_foreign_keys;"),
            System.Globalization.CultureInfo.InvariantCulture) != 0;

        return new ResetConnectionState(
            options.RestartIdentity, inTransaction, foreignKeys, sequence, SqliteDeferForeignKeys: deferred);
    }

    private const string SqliteSequenceExists =
        "SELECT COUNT(*) FROM \"main\".\"sqlite_master\" WHERE \"type\" = 'table' AND \"name\" = 'sqlite_sequence';";

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

    private const string MySqlNoBackslashEscapes = "SELECT FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0;";

    // The keys the verify step checks. A DELETE trigger on a reset table could write to any table, so
    // when there is one every key in the database is read, and those outside the reset are counted now,
    // before anything is deleted: a violation that was already there is not the reset's to refuse over.
    private static async Task<ResetConnectionState> WithMySqlForeignKeysAsync(
        DbConnection connection, DbTransaction? transaction, ResetPlan plan, ResetConnectionState state, CancellationToken cancellationToken)
    {
        var nbe = state.MySqlNoBackslashEscapes;
        var triggers = Convert.ToInt64(
            await ScalarAsync(connection, transaction, DatabaseForeignKeys.MySqlDeleteTriggers(plan, nbe), cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
        var keys = DatabaseForeignKeys.GroupMySqlForeignKeys(
            await ReadRowsAsync(connection, transaction, DatabaseForeignKeys.MySqlForeignKeys(plan, nbe, everyTable: triggers), cancellationToken)
                .ConfigureAwait(false));
        var outside = keys.Where(k => !k.InPlan).ToList();
        var before = outside.Count == 0
            ? []
            : (await ReadRowsAsync(connection, transaction, MySqlCounts(outside), cancellationToken).ConfigureAwait(false))[0];
        return state with { MySqlForeignKeys = WithCounts(keys, outside, before) };
    }

    private static ResetConnectionState WithMySqlForeignKeys(
        DbConnection connection, DbTransaction? transaction, ResetPlan plan, ResetConnectionState state)
    {
        var nbe = state.MySqlNoBackslashEscapes;
        var triggers = Convert.ToInt64(
            Scalar(connection, transaction, DatabaseForeignKeys.MySqlDeleteTriggers(plan, nbe)),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
        var keys = DatabaseForeignKeys.GroupMySqlForeignKeys(
            ReadRows(connection, transaction, DatabaseForeignKeys.MySqlForeignKeys(plan, nbe, everyTable: triggers)));
        var outside = keys.Where(k => !k.InPlan).ToList();
        var before = outside.Count == 0 ? [] : ReadRows(connection, transaction, MySqlCounts(outside))[0];
        return state with { MySqlForeignKeys = WithCounts(keys, outside, before) };
    }

    private static string MySqlCounts(IEnumerable<MySqlForeignKey> keys)
        => "SELECT " + string.Join(", ", keys.Select(ResetSqlGenerator.MySqlViolations)) + ";";

    private static List<MySqlForeignKey> WithCounts(List<MySqlForeignKey> keys, List<MySqlForeignKey> outside, string[] before)
        => [.. keys.Select(k => k.InPlan
            ? k
            : k with { Before = long.Parse(before[outside.IndexOf(k)], System.Globalization.CultureInfo.InvariantCulture) })];

    private static async Task<List<string[]>> ReadRowsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        var command = CreateCommand(connection, transaction, sql);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows.Add(Row(reader));
                }
            }
        }

        return rows;
    }

    private static List<string[]> ReadRows(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var rows = new List<string[]>();
        using var command = CreateCommand(connection, transaction, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(Row(reader));
        }

        return rows;
    }

    private static string[] Row(DbDataReader reader)
    {
        var row = new string[reader.FieldCount];
        for (var i = 0; i < row.Length; i++)
        {
            row[i] = Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        return row;
    }

    // PostgreSQL aborts the caller's whole transaction on any error, a cancelled or timed-out catalog
    // query included. Inside one, the check runs under a savepoint of its own, so a failure there costs
    // the caller nothing it had already done.
    private const string CheckSavepoint = "ef_toolkit_reset_check";

    private static async Task<T> GuardedAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        bool inTransaction,
        Func<CancellationToken, Task<T>> read,
        CancellationToken cancellationToken)
    {
        if (!inTransaction)
        {
            return await read(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteRawAsync(connection, transaction, $"SAVEPOINT {CheckSavepoint};", CancellationToken.None).ConfigureAwait(false);
        T result;
        try
        {
            result = await read(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ExecuteRawAsync(connection, transaction, $"ROLLBACK TO SAVEPOINT {CheckSavepoint};", CancellationToken.None).ConfigureAwait(false);
            await ExecuteRawAsync(connection, transaction, $"RELEASE SAVEPOINT {CheckSavepoint};", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await ExecuteRawAsync(connection, transaction, $"RELEASE SAVEPOINT {CheckSavepoint};", CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static T Guarded<T>(DbConnection connection, DbTransaction? transaction, bool inTransaction, Func<T> read)
    {
        if (!inTransaction)
        {
            return read();
        }

        ExecuteRaw(connection, transaction, $"SAVEPOINT {CheckSavepoint};");
        T result;
        try
        {
            result = read();
        }
        catch
        {
            ExecuteRaw(connection, transaction, $"ROLLBACK TO SAVEPOINT {CheckSavepoint};");
            ExecuteRaw(connection, transaction, $"RELEASE SAVEPOINT {CheckSavepoint};");
            throw;
        }

        ExecuteRaw(connection, transaction, $"RELEASE SAVEPOINT {CheckSavepoint};");
        return result;
    }

    private static async Task ExecuteRawAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        var command = CreateCommand(connection, transaction, sql);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ExecuteRaw(DbConnection connection, DbTransaction? transaction, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static async Task<List<DatabaseReference>> ReadReferencesAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        var references = new List<DatabaseReference>();
        var command = CreateCommand(connection, transaction, sql);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    references.Add(DatabaseForeignKeys.Read(reader));
                }
            }
        }

        return references;
    }

    private static List<DatabaseReference> ReadReferences(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var references = new List<DatabaseReference>();
        using var command = CreateCommand(connection, transaction, sql);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            references.Add(DatabaseForeignKeys.Read(reader));
        }

        return references;
    }
}
