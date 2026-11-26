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

                if (engine == DatabaseEngine.SqlServer)
                {
                    RefuseNotTables(
                        await ReadRowsAsync(connection, transaction, DatabaseForeignKeys.SqlServerNotTables(plan), cancellationToken).ConfigureAwait(false));
                }

                if (engine == DatabaseEngine.Sqlite)
                {
                    RefuseNotTables(
                        await ReadRowsAsync(connection, transaction, DatabaseForeignKeys.SqliteViews(plan), cancellationToken).ConfigureAwait(false));
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
                    state = state with
                    {
                        PostgreSqlTruncateTriggered = TruncateTriggered(plan, await GuardedAsync(
                                connection,
                                transaction,
                                state.InTransaction,
                                token => ReadRowsAsync(connection, transaction, DatabaseForeignKeys.PostgreSqlTruncateTriggers(plan), token),
                                cancellationToken)
                            .ConfigureAwait(false)),
                    };
                }

                if (engine == DatabaseEngine.MySql)
                {
                    RefuseMySqlPartialVisibility(Convert.ToInt64(
                        await ScalarAsync(connection, transaction, DatabaseForeignKeys.MySqlSeesEveryTable(state.MySqlNoBackslashEscapes), cancellationToken)
                            .ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture));
                    RefuseNotTables(
                        await ReadRowsAsync(connection, transaction, DatabaseForeignKeys.MySqlNotTransactionalTables(plan, state.MySqlNoBackslashEscapes), cancellationToken)
                            .ConfigureAwait(false), storageEngines: true);
                    await RefuseMySqlTemporaryAsync(connection, transaction, plan.Tables, cancellationToken).ConfigureAwait(false);

                    if (ResetSqlGenerator.MySqlVerifies(plan, state))
                    {
                        state = await WithMySqlForeignKeysAsync(connection, transaction, plan, state, cancellationToken).ConfigureAwait(false);
                    }
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

                if (engine == DatabaseEngine.SqlServer)
                {
                    RefuseNotTables(ReadRows(connection, transaction, DatabaseForeignKeys.SqlServerNotTables(plan)));
                }

                if (engine == DatabaseEngine.Sqlite)
                {
                    RefuseNotTables(ReadRows(connection, transaction, DatabaseForeignKeys.SqliteViews(plan)));
                }

                if (engine == DatabaseEngine.PostgreSql)
                {
                    ResetPlan.RefuseTruncatedDescendants(Guarded(
                        connection,
                        transaction,
                        state.InTransaction,
                        () => ReadReferences(connection, transaction, DatabaseForeignKeys.PostgreSqlDescendants(plan))));
                    state = state with
                    {
                        PostgreSqlTruncateTriggered = TruncateTriggered(plan, Guarded(
                            connection,
                            transaction,
                            state.InTransaction,
                            () => ReadRows(connection, transaction, DatabaseForeignKeys.PostgreSqlTruncateTriggers(plan)))),
                    };
                }

                if (engine == DatabaseEngine.MySql)
                {
                    RefuseMySqlPartialVisibility(Convert.ToInt64(
                        Scalar(connection, transaction, DatabaseForeignKeys.MySqlSeesEveryTable(state.MySqlNoBackslashEscapes)),
                        System.Globalization.CultureInfo.InvariantCulture));
                    RefuseNotTables(
                        ReadRows(connection, transaction, DatabaseForeignKeys.MySqlNotTransactionalTables(plan, state.MySqlNoBackslashEscapes)), storageEngines: true);
                    RefuseMySqlTemporary(connection, transaction, plan.Tables);

                    if (ResetSqlGenerator.MySqlVerifies(plan, state))
                    {
                        state = WithMySqlForeignKeys(connection, transaction, plan, state);
                    }
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

    // MySQL resolves a name to the session's temporary table of that name first, even qualified with the
    // database, and has no syntax for the permanent one behind it: the reset would empty the temporary
    // table, report success, and leave every row it was asked to remove. A table the verification reads
    // is no different — an empty temporary one would hide the permanent table's violations — so both ends
    // of every key it checks are refused the same way.
    private static async Task RefuseMySqlTemporaryAsync(
        DbConnection connection, DbTransaction? transaction, IEnumerable<ResetTable> tables, CancellationToken cancellationToken)
    {
        foreach (var table in tables)
        {
            RefuseMySqlTemporary(
                table,
                await ReadRowsAsync(connection, transaction, ResetSqlGenerator.MySqlShowCreate(table), cancellationToken).ConfigureAwait(false));
        }
    }

    private static void RefuseMySqlTemporary(DbConnection connection, DbTransaction? transaction, IEnumerable<ResetTable> tables)
    {
        foreach (var table in tables)
        {
            RefuseMySqlTemporary(table, ReadRows(connection, transaction, ResetSqlGenerator.MySqlShowCreate(table)));
        }
    }

    private static IEnumerable<ResetTable> TablesOf(IEnumerable<MySqlForeignKey> keys)
        => keys
            .SelectMany(k => new[] { new ResetTable(k.TableSchema, k.Table), new ResetTable(k.ReferencedSchema, k.ReferencedTable) })
            .Distinct();

    // A view under a name the model maps as a table would be deleted through, into whatever base table it
    // reads — one the reset was never asked to touch — and on SQLite an INSTEAD OF trigger would run in its
    // place. MyISAM and MEMORY tables ignore transactions: a reset that failed later would roll back every
    // other table and leave these emptied for good, the half-done state the unit exists to rule out.
    private static void RefuseNotTables(List<string[]> rows, bool storageEngines = false)
    {
        if (rows.FirstOrDefault() is not { } row)
        {
            return;
        }

        if (storageEngines && row[1] != "VIEW")
        {
            throw new InvalidOperationException(
                $"Cannot reset '{row[0]}': it uses the {row[1]} storage engine, which does not support transactions, "
                + "so a reset that failed part way could not put its rows back. Convert it to InnoDB "
                + "(ALTER TABLE ... ENGINE = InnoDB), or exclude it from the reset.");
        }

        throw new InvalidOperationException(
            $"Cannot reset '{row[0]}': the model maps it as a table, but in the database it is a "
            + $"{row[1].ToLowerInvariant().Replace('_', ' ')}, and deleting from it would delete from whatever it "
            + "stands for instead. Exclude it from the reset, or map it with ToView.");
    }

    private static void RefuseMySqlTemporary(ResetTable table, List<string[]> showCreate)
    {
        if (showCreate.Count > 0 && showCreate[0].Length > 1
            && showCreate[0][1].StartsWith("CREATE TEMPORARY TABLE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Cannot reset '{table}': this connection has a temporary table of that name, and MySQL resolves "
                + "the name to it rather than to the table the model maps, so the reset would empty the temporary "
                + "table and leave the real one as it is. Drop the temporary table, or reset on another connection.");
        }
    }

    private static void RefuseMySqlPartialVisibility(long seesEveryTable)
    {
        if (seesEveryTable == 0)
        {
            throw new InvalidOperationException(
                "Cannot reset: this login holds no privilege on the whole database, only on some of its tables. The "
                + "tables it cannot see, and their foreign keys, are hidden from it, so rows there could be deleted by "
                + "an ON DELETE CASCADE, or orphaned while checks are off for a foreign-key cycle, with nothing able "
                + "to check them. Grant the login a privilege on the database itself (for example "
                + "GRANT SELECT ON `db`.* TO it) or run the reset as the database's owner.");
        }
    }

    // A TRUNCATE trigger can put rows back, so such a table is counted before the commit — but under
    // row-level security the count sees only what the session may, and could be zero with rows left.
    internal static List<ResetTable> TruncateTriggered(ResetPlan plan, List<string[]> rows)
    {
        var tables = new List<ResetTable>();
        foreach (var row in rows)
        {
            var table = plan.Tables[int.Parse(row[0], System.Globalization.CultureInfo.InvariantCulture)];
            if (row[1] != "0")
            {
                throw new InvalidOperationException(
                    $"Cannot reset '{table}': it has a TRUNCATE trigger, which could put rows back, and row-level "
                    + "security applies to this login on it, so the reset could not see whether the table was left "
                    + "empty. Run the reset as the table's owner (without FORCE ROW LEVEL SECURITY), as a role with "
                    + "BYPASSRLS, or drop the trigger.");
            }

            tables.Add(table);
        }

        return tables;
    }

    private const string MySqlNoBackslashEscapes = "SELECT FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0;";

    // The keys the verify step checks: every one in the database, since a DELETE trigger on a reset table
    // could write to any table. Whether there is such a trigger cannot be asked — information_schema
    // shows a trigger only to a user with the TRIGGER privilege on its table, while the trigger fires
    // regardless, as its definer. Keys from tables outside the reset are counted now, before anything is
    // deleted, and must have no violations yet.
    private static async Task<ResetConnectionState> WithMySqlForeignKeysAsync(
        DbConnection connection, DbTransaction? transaction, ResetPlan plan, ResetConnectionState state, CancellationToken cancellationToken)
    {
        var keys = DatabaseForeignKeys.GroupMySqlForeignKeys(
            await ReadRowsAsync(connection, transaction, DatabaseForeignKeys.MySqlForeignKeys(plan, state.MySqlNoBackslashEscapes), cancellationToken)
                .ConfigureAwait(false));
        await RefuseMySqlTemporaryAsync(connection, transaction, TablesOf(keys), cancellationToken).ConfigureAwait(false);
        var outside = keys.Where(k => !k.InPlan).ToList();
        if (outside.Count > 0)
        {
            RefuseExistingViolations(outside, (await ReadRowsAsync(connection, transaction, MySqlCounts(outside), cancellationToken).ConfigureAwait(false))[0]);
        }

        return state with { MySqlForeignKeys = keys };
    }

    private static ResetConnectionState WithMySqlForeignKeys(
        DbConnection connection, DbTransaction? transaction, ResetPlan plan, ResetConnectionState state)
    {
        var keys = DatabaseForeignKeys.GroupMySqlForeignKeys(
            ReadRows(connection, transaction, DatabaseForeignKeys.MySqlForeignKeys(plan, state.MySqlNoBackslashEscapes)));
        RefuseMySqlTemporary(connection, transaction, TablesOf(keys));
        var outside = keys.Where(k => !k.InPlan).ToList();
        if (outside.Count > 0)
        {
            RefuseExistingViolations(outside, ReadRows(connection, transaction, MySqlCounts(outside))[0]);
        }

        return state with { MySqlForeignKeys = keys };
    }

    private static string MySqlCounts(IEnumerable<MySqlForeignKey> keys)
        => "SELECT " + string.Join(", ", keys.Select(ResetSqlGenerator.MySqlViolations)) + ";";

    // With checks off, a trigger could replace one orphan of a key with another and leave the count as it
    // was; no count tells those apart, and rows need not have a key to compare. So a key outside the reset
    // must start with no violations, and then any violation after it is the reset's.
    private static void RefuseExistingViolations(List<MySqlForeignKey> outside, string[] counts)
    {
        for (var i = 0; i < outside.Count; i++)
        {
            if (long.Parse(counts[i], System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                var key = outside[i];
                var table = key.TableSchema is null ? key.Table : $"{key.TableSchema}.{key.Table}";
                throw new InvalidOperationException(
                    $"Cannot reset: '{table}' already has rows that violate its foreign key to "
                    + $"'{key.ReferencedSchema}.{key.ReferencedTable}', and this reset must switch MySQL's foreign-key "
                    + "checks off for a foreign-key cycle. With violations already there, one written during the reset "
                    + "could not be told apart from them. Fix or remove those rows first.");
            }
        }
    }

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
