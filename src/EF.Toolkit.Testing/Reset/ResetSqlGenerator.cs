namespace EFToolkit.Testing;

/// <summary>What the reset knows about the connection it is about to run on.</summary>
/// <param name="RestartIdentity">Whether identity counters restart.</param>
/// <param name="InTransaction">
///     Whether a transaction is open. Decides the MySQL statements — <c>TRUNCATE</c> commits it — and
///     the SQLite pragma, since <c>foreign_keys</c> cannot change inside one.
/// </param>
/// <param name="SqliteForeignKeysEnabled">The current value of SQLite's <c>PRAGMA foreign_keys</c>.</param>
/// <param name="SqliteHasSequenceTable">
///     Whether <c>sqlite_sequence</c> exists. SQLite creates it on the first <c>AUTOINCREMENT</c> table
///     and refuses to name it before then.
/// </param>
/// <param name="MySqlForeignKeyChecks">
///     The session's <c>@@FOREIGN_KEY_CHECKS</c>, read beforehand so it can be restored as a literal.
///     A user variable would save the round trip, but both MySQL client libraries refuse
///     <c>@name</c> as an undefined parameter unless the connection string opts in to user variables.
/// </param>
internal sealed record ResetConnectionState(
    bool RestartIdentity,
    bool InTransaction,
    bool SqliteForeignKeysEnabled = true,
    bool SqliteHasSequenceTable = false,
    bool MySqlForeignKeyChecks = true);

/// <summary>The statements a reset runs, in three parts.</summary>
/// <param name="Prologue">Suspends whatever has to be suspended.</param>
/// <param name="Body">Empties the tables and restarts their counters.</param>
/// <param name="Epilogue">
///     Restores what the prologue suspended. Run even when the body fails: the settings it restores
///     are per connection, and a pooled connection handed back with foreign-key checks off would
///     silently disable them for whichever test opened it next.
/// </param>
internal sealed record ResetScript(
    IReadOnlyList<string> Prologue,
    IReadOnlyList<string> Body,
    IReadOnlyList<string> Epilogue)
{
    public static ResetScript Empty { get; } = new([], [], []);

    /// <summary>Every statement, in execution order.</summary>
    public IEnumerable<string> All => Prologue.Concat(Body).Concat(Epilogue);
}

/// <summary>Turns a <see cref="ResetPlan" /> into SQL for one engine.</summary>
/// <remarks>
///     Separate from execution so that what each engine is sent can be tested without the engine.
///     Each statement is its own command: a multi-statement batch is not something every MySQL
///     provider accepts by default, and one command per statement lets the epilogue run whatever
///     the body did.
/// </remarks>
internal static class ResetSqlGenerator
{
    public static ResetScript Generate(DatabaseEngine engine, ResetPlan plan, ResetConnectionState state)
    {
        if (plan.Tables.Count == 0)
        {
            return ResetScript.Empty;
        }

        return engine switch
        {
            DatabaseEngine.PostgreSql => PostgreSql(plan, state),
            DatabaseEngine.SqlServer => SqlServer(plan, state),
            DatabaseEngine.MySql => MySql(plan, state),
            DatabaseEngine.Sqlite => Sqlite(plan, state),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
        };
    }

    /// <remarks>
    ///     One statement for every table: <c>TRUNCATE</c> takes the whole list at once, checks the
    ///     foreign keys between them as a set, and is transactional on PostgreSQL, so it needs no
    ///     ordering and works inside an isolated scope. <c>CASCADE</c> is left off once anything is
    ///     excluded — it would otherwise empty an excluded table that references a reset one, quietly
    ///     undoing the exclusion. Without it, that case is an error instead.
    /// </remarks>
    private static ResetScript PostgreSql(ResetPlan plan, ResetConnectionState state)
    {
        var tables = string.Join(", ", plan.Tables.Select(t => Quote(t, '"', '"')));
        var identity = state.RestartIdentity ? " RESTART IDENTITY" : "";
        var cascade = plan.HasExclusions ? "" : " CASCADE";

        return new ResetScript([], [$"TRUNCATE TABLE {tables}{identity}{cascade};"], []);
    }

    /// <remarks>
    ///     <para>
    ///         SQL Server refuses <c>TRUNCATE</c> on any table a foreign key references, so rows go
    ///         by <c>DELETE</c> in dependency order. Only tables on a cycle have their constraints
    ///         suspended: everywhere else the order alone is enough, and leaving the constraints on
    ///         means a row in a table outside the reset still fails the delete rather than being
    ///         orphaned.
    ///     </para>
    ///     <para>
    ///         The reseed has two cases because <c>DBCC CHECKIDENT ... RESEED, n</c> means two things:
    ///         on a table that has never generated a value the next row gets <c>n</c>, and on one that
    ///         has, <c>n</c> plus the increment. Which applies is read off
    ///         <c>sys.identity_columns.last_value</c>, and the seed and increment come from the table
    ///         rather than being assumed to be 1. Tables without an identity column are skipped at run
    ///         time, which is the one source of truth — the model only says what EF generates.
    ///     </para>
    /// </remarks>
    private static ResetScript SqlServer(ResetPlan plan, ResetConnectionState state)
    {
        var cyclic = plan.Tables.Where(plan.CyclicTables.Contains).ToList();

        var prologue = cyclic
            .Select(t => $"ALTER TABLE {Quote(t, '[', ']')} NOCHECK CONSTRAINT ALL;")
            .ToList();

        var body = plan.Tables
            .Select(t => $"DELETE FROM {Quote(t, '[', ']')};")
            .ToList();

        if (state.RestartIdentity)
        {
            body.AddRange(plan.Tables.Select(t =>
            {
                var name = SqlServerLiteral(Quote(t, '[', ']'));
                return $"""
                    IF OBJECTPROPERTY(OBJECT_ID({name}), 'TableHasIdentity') = 1
                    BEGIN
                        DECLARE @reseed BIGINT = IDENT_SEED({name});
                        IF (SELECT last_value FROM sys.identity_columns WHERE object_id = OBJECT_ID({name})) IS NOT NULL
                            SET @reseed = @reseed - IDENT_INCR({name});
                        DBCC CHECKIDENT({name}, RESEED, @reseed) WITH NO_INFOMSGS;
                    END;
                    """;
            }));
        }

        var epilogue = cyclic
            .Select(t => $"ALTER TABLE {Quote(t, '[', ']')} WITH CHECK CHECK CONSTRAINT ALL;")
            .ToList();

        return new ResetScript(prologue, body, epilogue);
    }

    /// <remarks>
    ///     <c>TRUNCATE</c> is the only statement that restarts <c>AUTO_INCREMENT</c> without DDL, and
    ///     both it and <c>ALTER TABLE</c> commit any open transaction implicitly. Inside a transaction
    ///     the rows are deleted instead and the counters keep going: an isolated scope that silently
    ///     committed would be worse than one whose ids are not 1. The check setting is saved and
    ///     left alone when it was already off, so a session that had it off keeps it off.
    /// </remarks>
    private static ResetScript MySql(ResetPlan plan, ResetConnectionState state)
    {
        var truncate = state.RestartIdentity && !state.InTransaction;

        var body = plan.Tables
            .Select(t => truncate
                ? $"TRUNCATE TABLE {Quote(t, '`', '`')};"
                : $"DELETE FROM {Quote(t, '`', '`')};")
            .ToList();

        return state.MySqlForeignKeyChecks
            ? new ResetScript(["SET FOREIGN_KEY_CHECKS = 0;"], body, ["SET FOREIGN_KEY_CHECKS = 1;"])
            : new ResetScript([], body, []);
    }

    /// <remarks>
    ///     <c>PRAGMA foreign_keys</c> is a no-op inside a transaction, so there the checks are
    ///     deferred to commit instead — by which time every row is gone and there is nothing to
    ///     violate — and switched back to immediate afterwards, so the rest of the transaction is
    ///     checked as usual. Counters live in <c>sqlite_sequence</c>, and only for
    ///     <c>AUTOINCREMENT</c> tables; a plain <c>INTEGER PRIMARY KEY</c> restarts on its own once
    ///     the table is empty.
    /// </remarks>
    private static ResetScript Sqlite(ResetPlan plan, ResetConnectionState state)
    {
        var body = plan.Tables
            .Select(t => $"DELETE FROM {Quote(t, '"', '"')};")
            .ToList();

        if (state.RestartIdentity && state.SqliteHasSequenceTable)
        {
            var names = string.Join(", ", plan.Tables.Select(t => SqlLiteral(t.Name)));
            body.Add($"DELETE FROM \"sqlite_sequence\" WHERE \"name\" IN ({names});");
        }

        if (state.InTransaction)
        {
            return new ResetScript(
                ["PRAGMA defer_foreign_keys = ON;"],
                body,
                ["PRAGMA defer_foreign_keys = OFF;"]);
        }

        return state.SqliteForeignKeysEnabled
            ? new ResetScript(["PRAGMA foreign_keys = OFF;"], body, ["PRAGMA foreign_keys = ON;"])
            : new ResetScript([], body, []);
    }

    private static string Quote(ResetTable table, char open, char close)
    {
        var name = Quote(table.Name, open, close);
        return table.Schema is null ? name : $"{Quote(table.Schema, open, close)}.{name}";
    }

    private static string Quote(string identifier, char open, char close)
        => open + identifier.Replace(close.ToString(), new string(close, 2), StringComparison.Ordinal) + close;

    private static string SqlLiteral(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string SqlServerLiteral(string value) => "N" + SqlLiteral(value);
}
