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
/// <param name="SqliteDeferForeignKeys">
///     The current value of SQLite's <c>PRAGMA defer_foreign_keys</c>, restored after a reset inside a
///     transaction rather than switched off regardless.
/// </param>
/// <param name="MySqlForeignKeyChecks">
///     The session's <c>@@FOREIGN_KEY_CHECKS</c>, read beforehand so it can be restored as a literal.
///     A user variable would save the round trip, but both MySQL client libraries refuse
///     <c>@name</c> as an undefined parameter unless the connection string opts in to user variables.
/// </param>
/// <param name="MySqlForeignKeys">
///     MySQL: every foreign key in the database, to verify — read only when the reset deletes with checks
///     off, since a <c>DELETE</c> trigger on a reset table could write anywhere. <see langword="null" />
///     when not read.
/// </param>
/// <param name="PostgreSqlTruncateTriggered">
///     PostgreSQL: the reset tables with a <c>TRUNCATE</c> trigger, the one kind that can write after the
///     <c>TRUNCATE</c> — and put rows back — so these are checked empty before the commit.
/// </param>
/// <param name="MySqlNoBackslashEscapes">
///     Whether the session's <c>sql_mode</c> has <c>NO_BACKSLASH_ESCAPES</c>, under which a backslash
///     in a string literal is itself rather than an escape — so the catalog queries' table names are
///     written to match.
/// </param>
internal sealed record ResetConnectionState(
    bool RestartIdentity,
    bool InTransaction,
    bool SqliteForeignKeysEnabled = true,
    bool SqliteHasSequenceTable = false,
    bool MySqlForeignKeyChecks = true,
    bool SqliteDeferForeignKeys = false,
    IReadOnlyList<MySqlForeignKey>? MySqlForeignKeys = null,
    bool MySqlNoBackslashEscapes = false,
    IReadOnlyList<ResetTable>? PostgreSqlTruncateTriggered = null);

/// <summary>A MySQL foreign key from a reset table, as the catalog records it.</summary>
/// <param name="Table">The referencing table.</param>
/// <param name="Columns">Its columns, in key order.</param>
/// <param name="ReferencedSchema">The referenced table's database.</param>
/// <param name="ReferencedTable">The referenced table.</param>
/// <param name="ReferencedColumns">The referenced columns, in key order.</param>
/// <param name="InPlan">Whether <paramref name="Table" /> is one the reset empties.</param>
/// <param name="TableSchema">
///     The referencing table's database when it is not the connection's — a trigger can write to
///     another database just as well.
/// </param>
internal sealed record MySqlForeignKey(
    string Table,
    IReadOnlyList<string> Columns,
    string ReferencedSchema,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    bool InPlan = true,
    string? TableSchema = null);

/// <summary>The statements a reset runs, in three parts.</summary>
internal sealed record ResetScript
{
    /// <summary>Initializes a new instance of the <see cref="ResetScript" /> record.</summary>
    /// <param name="prologue">Suspends whatever has to be suspended.</param>
    /// <param name="body">Empties the tables and restarts their counters.</param>
    /// <param name="epilogue">
    ///     Restores what the prologue suspended, one statement per prologue statement. Run even when the
    ///     body fails — or the prologue part way through — for the statements that took effect: the
    ///     settings they restore are per connection or per table, and a pooled connection handed back with
    ///     foreign-key checks off would silently disable them for whichever test opened it next.
    /// </param>
    public ResetScript(IReadOnlyList<string> prologue, IReadOnlyList<string> body, IReadOnlyList<string> epilogue)
    {
        // The runner undoes Prologue[i] with Epilogue[i], and only for the statements that ran.
        if (prologue.Count != epilogue.Count)
        {
            throw new ArgumentException("Every prologue statement needs exactly one epilogue statement to undo it.", nameof(epilogue));
        }

        Prologue = prologue;
        Body = body;
        Epilogue = epilogue;
    }

    public IReadOnlyList<string> Prologue { get; }

    public IReadOnlyList<string> Body { get; }

    public IReadOnlyList<string> Epilogue { get; }

    /// <summary>
    ///     Opens the unit the body runs in — a transaction, or a savepoint inside the caller's — after
    ///     the prologue, since some settings (SQLite's <c>foreign_keys</c>) cannot change inside one.
    /// </summary>
    public IReadOnlyList<string> Begin { get; init; } = [];

    /// <summary>
    ///     Queries run after the body and before the commit, each of which must count zero. Where the
    ///     reset suspended foreign-key checks, a trigger on a reset table could have written a row that
    ///     violates one, and nothing would ever re-check it; a non-zero count fails the reset and rolls
    ///     it back.
    /// </summary>
    public IReadOnlyList<string> Verify { get; init; } = [];

    /// <summary>
    ///     Queries run with <see cref="Verify" />, one per table, each counting the rows left in that table,
    ///     which must be none: a trigger can keep a row the delete was meant to remove.
    /// </summary>
    public IReadOnlyList<(string Sql, string Table)> VerifyEmpty { get; init; } = [];

    /// <summary>Closes the unit once the body has succeeded.</summary>
    public IReadOnlyList<string> Commit { get; init; } = [];

    /// <summary>
    ///     Run after the commit, for what cannot be part of the unit — MySQL's counter restart is DDL,
    ///     which commits on its own. A failure here is reported; the rows are already gone.
    /// </summary>
    public IReadOnlyList<string> AfterCommit { get; init; } = [];

    /// <summary>
    ///     Undoes the body if it failed, before the epilogue restores anything — so a reset that fails
    ///     part way never leaves some tables emptied and others not, with the checks that would have
    ///     caught the orphans switched back on only after the fact.
    /// </summary>
    public IReadOnlyList<string> Rollback { get; init; } = [];

    public static ResetScript Empty { get; } = new([], [], []);

    /// <summary>Every statement of a successful run, in execution order.</summary>
    public IEnumerable<string> All
        => Prologue.Concat(Begin).Concat(Body).Concat(Verify).Concat(VerifyEmpty.Select(v => v.Sql))
            .Concat(Commit).Concat(AfterCommit).Concat(Epilogue.Reverse());
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
    /// <summary>A fresh name for the savepoint a reset inside the caller's transaction runs under.</summary>
    /// <remarks>
    ///     Never a fixed one: MySQL replaces a savepoint of the same name, so a caller's own savepoint
    ///     called that would be lost. Short enough for SQL Server, which allows 32 characters.
    /// </remarks>
    internal static string NewSavepoint() => "ef_reset_" + Guid.NewGuid().ToString("N")[..16];

    /// <param name="engine">The engine.</param>
    /// <param name="plan">The tables.</param>
    /// <param name="state">The connection.</param>
    /// <param name="savepoint">The savepoint name; a fresh one when <see langword="null" />.</param>
    public static ResetScript Generate(DatabaseEngine engine, ResetPlan plan, ResetConnectionState state, string? savepoint = null)
    {
        if (plan.Tables.Count == 0)
        {
            return ResetScript.Empty;
        }

        savepoint ??= NewSavepoint();
        return engine switch
        {
            DatabaseEngine.PostgreSql => PostgreSql(plan, state, savepoint),
            DatabaseEngine.SqlServer => SqlServer(plan, state, savepoint),
            DatabaseEngine.MySql => MySql(plan, state, savepoint),
            DatabaseEngine.Sqlite => Sqlite(plan, state, savepoint),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
        };
    }

    /// <remarks>
    ///     One statement for every table: <c>TRUNCATE</c> takes the whole list at once, checks the
    ///     foreign keys between them as a set, and is transactional on PostgreSQL, so it needs no
    ///     ordering and works inside an isolated scope. There is no <c>CASCADE</c>: see the comment below.
    /// </remarks>
    private static ResetScript PostgreSql(ResetPlan plan, ResetConnectionState state, string savepoint)
    {
        var tables = string.Join(", ", plan.Tables.Select(t => Quote(t, '"', '"')));
        var identity = state.RestartIdentity ? " RESTART IDENTITY" : "";

        // No CASCADE: it would also empty any table outside the model that references one of these,
        // which a reset of the mapped tables was never asked to touch. Without it, such a table makes
        // the TRUNCATE fail and names itself, and the tables listed are still checked as one set.
        var script = new ResetScript([], [$"TRUNCATE TABLE {tables}{identity};"], []);

        // One statement needs no unit of its own — but inside the caller's transaction, any error aborts
        // the whole transaction on PostgreSQL. A savepoint keeps a refused reset from costing the caller
        // the work it did before.
        // Row triggers do not fire for TRUNCATE, but a statement-level TRUNCATE trigger runs after it and
        // can insert rows straight back; those tables are counted inside a unit, so a non-empty one rolls
        // the TRUNCATE back too.
        var triggered = state.PostgreSqlTruncateTriggered ?? [];
        if (triggered.Count > 0)
        {
            script = script with
            {
                VerifyEmpty = [.. triggered.Select(t => ($"SELECT COUNT(*) FROM {Quote(t, '"', '"')};", t.ToString()))],
            };
        }

        if (state.InTransaction)
        {
            return script with
            {
                Begin = [$"SAVEPOINT {savepoint};"],
                Commit = [$"RELEASE SAVEPOINT {savepoint};"],
                Rollback = [$"ROLLBACK TO SAVEPOINT {savepoint};"],
            };
        }

        return triggered.Count > 0
            ? script with { Begin = ["BEGIN;"], Commit = ["COMMIT;"], Rollback = ["ROLLBACK;"] }
            : script;
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
    ///         time, which is the one source of truth — the model only says what EF generates. The
    ///         arithmetic is <c>decimal(38, 0)</c>, the widest an identity column can be: a <c>bigint</c>
    ///         would overflow on a <c>decimal(20, 0)</c> identity seeded past its range.
    ///     </para>
    /// </remarks>
    private static ResetScript SqlServer(ResetPlan plan, ResetConnectionState state, string savepoint)
    {
        var cyclic = plan.Tables.Where(plan.CyclicTables.Contains).ToList();

        // Constraint changes are transactional on SQL Server, so suspending and re-checking them both
        // sit inside the unit with the deletes. A re-check that fails — a trigger left a row behind —
        // then rolls back the deletes and the NOCHECK together, instead of failing after a commit with
        // the rows gone and the constraint left off.
        var body = cyclic
            .Select(t => $"ALTER TABLE {Quote(t, '[', ']')} NOCHECK CONSTRAINT ALL;")
            .ToList();

        body.AddRange(plan.Tables.Select(t => $"DELETE FROM {Quote(t, '[', ']')};"));

        // An INSTEAD OF DELETE trigger runs in place of the delete — a soft delete marks the rows and keeps
        // them, unique keys and all — and the reset would report success with the table still full. Every
        // table must be empty before the unit commits; one that is not fails it, and it is rolled back.
        body.AddRange(plan.Tables.Select(t =>
            $"IF EXISTS (SELECT 1 FROM {Quote(t, '[', ']')}) THROW 50000, {SqlServerLiteral(
                $"The reset left rows in '{t}': an INSTEAD OF DELETE trigger, such as a soft delete, kept them. "
                + "It has been rolled back. Exclude the table from the reset, or disable the trigger for it.")}, 1;"));

        if (state.RestartIdentity)
        {
            body.AddRange(plan.Tables.Select(t =>
            {
                var name = SqlServerLiteral(Quote(t, '[', ']'));
                return $"""
                    IF OBJECTPROPERTY(OBJECT_ID({name}), 'TableHasIdentity') = 1
                    BEGIN
                        DECLARE @reseed decimal(38, 0) = IDENT_SEED({name});
                        IF (SELECT last_value FROM sys.identity_columns WHERE object_id = OBJECT_ID({name})) IS NOT NULL
                            SET @reseed = @reseed - IDENT_INCR({name});
                        DBCC CHECKIDENT({name}, RESEED, @reseed) WITH NO_INFOMSGS;
                    END;
                    """;
            }));
        }

        body.AddRange(cyclic.Select(t => $"ALTER TABLE {Quote(t, '[', ']')} WITH CHECK CHECK CONSTRAINT ALL;"));

        return new ResetScript([], body, [])
        {
            Begin = [state.InTransaction ? $"SAVE TRANSACTION {savepoint};" : "BEGIN TRANSACTION;"],
            Commit = state.InTransaction ? [] : ["COMMIT TRANSACTION;"],
            // An error can end the caller's transaction outright (a trigger's THROW rolls it all back) or
            // doom it; the savepoint is then gone or unusable, and only the caller can roll back.
            Rollback = [state.InTransaction
                ? $"IF XACT_STATE() = 1 ROLLBACK TRANSACTION {savepoint};"
                : "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;"],
        };
    }

    /// <remarks>
    ///     <para>
    ///         Rows go by <c>DELETE</c> in dependency order, as on SQL Server, inside one unit, with the
    ///         foreign-key checks left on: then the engine itself refuses a delete that would orphan a
    ///         row, wherever that row is — in a table the reset's login cannot even see — and a trigger
    ///         that writes an orphan fails the reset there and then. <c>TRUNCATE</c> would need the
    ///         checks off, since it refuses any table a foreign key references, and commits on its own.
    ///     </para>
    ///     <para>
    ///         Only a plan with a foreign-key cycle needs the checks off. With them off — suspended, or
    ///         already off for the session, which keeps them off — <see cref="ResetConnectionState.MySqlForeignKeys" />
    ///         are checked before the commit, since MySQL never re-checks rows written while checks were off.
    ///     </para>
    ///     <para>
    ///         Counters restart with <c>ALTER TABLE ... AUTO_INCREMENT = 1</c>, which on an emptied table
    ///         means the next row gets the first value the session's <c>auto_increment_offset</c> and
    ///         <c>auto_increment_increment</c> allow — 1 by default — after the commit: <c>ALTER TABLE</c> commits implicitly, so it
    ///         cannot run inside the unit. Inside the caller's transaction it would commit that too, so
    ///         there the counters keep going: an isolated scope that silently committed would be worse
    ///         than one whose ids are not 1.
    ///     </para>
    /// </remarks>
    private static ResetScript MySql(ResetPlan plan, ResetConnectionState state, string savepoint)
    {
        var body = plan.Tables.Select(t => $"DELETE FROM {Quote(t, '`', '`')};").ToList();

        var script = state.MySqlForeignKeyChecks && plan.CyclicTables.Count > 0
            ? new ResetScript(["SET FOREIGN_KEY_CHECKS = 0;"], body, ["SET FOREIGN_KEY_CHECKS = 1;"])
            : new ResetScript([], body, []);

        if (MySqlVerifies(plan, state))
        {
            script = script with { Verify = MySqlOrphans(state.MySqlForeignKeys ?? []) };
        }

        // A trigger on a table deleted later can write into one already emptied — an audit log, say —
        // so every table is counted before the unit closes.
        script = script with
        {
            VerifyEmpty = [.. plan.Tables.Select(t => ($"SELECT COUNT(*) FROM {Quote(t, '`', '`')};", t.ToString()))],
        };

        if (state.InTransaction)
        {
            return script with
            {
                Begin = [$"SAVEPOINT {savepoint};"],
                Commit = [$"RELEASE SAVEPOINT {savepoint};"],
                Rollback = [$"ROLLBACK TO SAVEPOINT {savepoint};"],
            };
        }

        return script with
        {
            Begin = ["START TRANSACTION;"],
            Commit = ["COMMIT;"],
            Rollback = ["ROLLBACK;"],
            AfterCommit = state.RestartIdentity
                ? [.. plan.Tables.Select(t => $"ALTER TABLE {Quote(t, '`', '`')} AUTO_INCREMENT = 1;")]
                : [],
        };
    }

    /// <summary>MySQL: the statement whose answer tells a temporary table from a permanent one.</summary>
    internal static string MySqlShowCreate(ResetTable table) => $"SHOW CREATE TABLE {Quote(table, '`', '`')};";

    /// <summary>
    ///     Whether a MySQL reset of <paramref name="plan" /> deletes with foreign-key checks off, and so
    ///     needs the catalog's foreign keys to verify against.
    /// </summary>
    internal static bool MySqlVerifies(ResetPlan plan, ResetConnectionState state)
        => plan.Tables.Count > 0 && (!state.MySqlForeignKeyChecks || plan.CyclicTables.Count > 0);

    // Every reset table is empty once the deletes are done, so any row there that violates a foreign key
    // was written during the reset — by a trigger — and none is acceptable. Elsewhere only a DELETE
    // trigger on a reset table can write while checks are off (the catalog preflight already refuses a
    // foreign key from outside into a reset table), and a key there must have had no violations before
    // the reset (the preflight refuses one that did): a count that merely did not grow could hide a
    // trigger replacing one orphan with another. So every key must have none. A key with a NULL column
    // is not enforced, as MySQL treats it.
    private static string[] MySqlOrphans(IReadOnlyList<MySqlForeignKey> foreignKeys)
        => foreignKeys.Count == 0 ? [] : ["SELECT " + string.Join(" + ", foreignKeys.Select(MySqlViolations)) + ";"];

    /// <summary>A query counting the rows that violate <paramref name="fk" />, parenthesised.</summary>
    internal static string MySqlViolations(MySqlForeignKey fk)
    {
        var present = string.Join(" AND ", fk.Columns.Select(c => $"c.{Quote(c, '`', '`')} IS NOT NULL"));
        var join = string.Join(" AND ", fk.Columns.Zip(fk.ReferencedColumns, (c, r) => $"p.{Quote(r, '`', '`')} = c.{Quote(c, '`', '`')}"));
        var parent = $"{Quote(fk.ReferencedSchema, '`', '`')}.{Quote(fk.ReferencedTable, '`', '`')}";
        var child = fk.TableSchema is null ? Quote(fk.Table, '`', '`') : $"{Quote(fk.TableSchema, '`', '`')}.{Quote(fk.Table, '`', '`')}";
        return $"(SELECT COUNT(*) FROM {child} AS c WHERE {present} "
            + $"AND NOT EXISTS (SELECT 1 FROM {parent} AS p WHERE {join}))";
    }

    /// <remarks>
    ///     <c>PRAGMA foreign_keys</c> is a no-op inside a transaction, so there the checks are deferred
    ///     instead, letting the deletes run in any order. The reset does not leave them to the commit: before
    ///     releasing its savepoint it checks every schema the connection has (main, temp, attached) against the snapshot taken at the start, so
    ///     a new violation — an orphan a <c>DELETE</c> trigger wrote — fails the reset itself and rolls it
    ///     back to the savepoint. Deferral is then restored to what the caller had, so the rest of the
    ///     transaction is checked as before. Counters live in <c>sqlite_sequence</c>, and only for
    ///     <c>AUTOINCREMENT</c> tables; a plain <c>INTEGER PRIMARY KEY</c> restarts on its own once
    ///     the table is empty.
    /// </remarks>
    private static ResetScript Sqlite(ResetPlan plan, ResetConnectionState state, string savepoint)
    {
        // Qualified with main, where EF creates every table: unqualified, a temporary table of the same
        // name would shadow it and be emptied instead of the table the plan names.
        // The snapshot of existing violations comes first, inside the unit: the verify step then fails
        // only on violations the reset itself caused, wherever in the database they are.
        // The snapshot's name is new for every reset, so it can never be a table the caller already has —
        // with a fixed name, dropping "ours" first would have destroyed theirs.
        var snapshot = $"ef_toolkit_fk_before_{Guid.NewGuid():N}";
        List<string> body = [SqliteViolationSnapshot(snapshot)];
        body.AddRange(plan.Tables.Select(t => $"DELETE FROM \"main\".{Quote(t.Name, '"', '"')};"));

        if (state.RestartIdentity && state.SqliteHasSequenceTable)
        {
            var names = string.Join(", ", plan.Tables.Select(t => SqlLiteral(t.Name)));
            // NOCASE, as SQLite matches identifiers: the DELETE above found Widgets under the model's
            // widgets, and an exact comparison here would leave its counter running.
            body.Add($"DELETE FROM \"main\".\"sqlite_sequence\" WHERE \"name\" COLLATE NOCASE IN ({names});");
        }

        if (state.InTransaction)
        {
            return new ResetScript(
                state.SqliteDeferForeignKeys ? [] : ["PRAGMA defer_foreign_keys = ON;"],
                body,
                state.SqliteDeferForeignKeys ? [] : ["PRAGMA defer_foreign_keys = OFF;"])
            {
                Begin = [$"SAVEPOINT {savepoint};"],
                Verify = [SqliteNewViolations(snapshot)],
                VerifyEmpty = SqliteEmpty(plan),
                Commit = [SqliteDropSnapshot(snapshot), $"RELEASE SAVEPOINT {savepoint};"],
                Rollback = [$"ROLLBACK TO SAVEPOINT {savepoint};", $"RELEASE SAVEPOINT {savepoint};"],
            };
        }

        var script = state.SqliteForeignKeysEnabled
            ? new ResetScript(["PRAGMA foreign_keys = OFF;"], body, ["PRAGMA foreign_keys = ON;"])
            : new ResetScript([], body, []);

        return script with
        {
            Begin = ["BEGIN;"],
            Verify = [SqliteNewViolations(snapshot)],
            VerifyEmpty = SqliteEmpty(plan),
            Commit = [SqliteDropSnapshot(snapshot), "COMMIT;"],
            Rollback = ["ROLLBACK;"],
        };
    }

    // Turning foreign_keys back on checks nothing already written, and switching defer_foreign_keys off
    // discards what was deferred — so a row a trigger wrote mid-reset pointing at a missing one would
    // otherwise be committed, in a reset table or any other (an audit trigger writing to its own table,
    // say). Every schema the connection has (main, temp, attached) is checked, in one statement however many tables there are, against a
    // snapshot taken at the start of the unit: violations that were already there are not the reset's
    // to refuse over. The snapshot is a temporary table, created inside the unit, so a rollback removes
    // it; the commit drops it.
    // Called as functions, with (): a table of the user's named pragma_foreign_key_check would otherwise
    // be read in the pragma's place, empty, and every violation would go unseen. With the parentheses
    // such a table cannot be called, and the statement fails instead.
    private static string SqliteViolationSnapshot(string name)
        => $"CREATE TEMP TABLE \"{name}\" AS SELECT c.s, c.t, c.r, c.f FROM {SqliteAllViolations} AS c;";

    // Every schema the connection has — main, temp and each attached database: a trigger on a reset table
    // can write anywhere, and an unqualified pragma_foreign_key_check() reads main alone. Called as
    // functions, so that a table of the same name cannot stand in for either.
    /// <summary>Counts every foreign-key violation in every schema the connection has.</summary>
    internal const string SqliteViolationCount = "SELECT COUNT(*) FROM " + SqliteAllViolations + " AS c;";

    private const string SqliteAllViolations =
        "(SELECT d.\"name\" AS s, k.\"table\" AS t, k.\"rowid\" AS r, k.\"fkid\" AS f "
        + "FROM pragma_database_list() AS d, pragma_foreign_key_check(NULL, d.\"name\") AS k)";

    // Rows with a rowid are compared one by one. A WITHOUT ROWID table reports no rowid, so its
    // violations cannot be told apart; for those the count per table and foreign key must not grow —
    // one old orphan must not hide any number of new ones.
    private static string SqliteNewViolations(string name)
    {
        var snapshot = $"\"temp\".\"{name}\"";
        return "SELECT "
            + $"(SELECT COUNT(*) FROM {SqliteAllViolations} AS c WHERE c.r IS NOT NULL AND NOT EXISTS ("
            + $"SELECT 1 FROM {snapshot} AS b WHERE b.s = c.s AND b.t = c.t AND b.r = c.r AND b.f = c.f))"
            + " + (SELECT COUNT(*) FROM ("
            + $"SELECT c.s, c.t, c.f, COUNT(*) AS n FROM {SqliteAllViolations} AS c "
            + "WHERE c.r IS NULL GROUP BY c.s, c.t, c.f) AS a "
            + $"WHERE a.n > (SELECT COUNT(*) FROM {snapshot} AS b WHERE b.s = a.s AND b.t = a.t AND b.f = a.f AND b.r IS NULL));";
    }

    // A BEFORE DELETE trigger that calls RAISE(IGNORE) skips the row without an error, so a protected
    // fixture would survive a reset that reported success.
    private static (string Sql, string Table)[] SqliteEmpty(ResetPlan plan)
        => [.. plan.Tables.Select(t => ($"SELECT COUNT(*) FROM \"main\".{Quote(t.Name, '"', '"')};", t.Name))];

    private static string SqliteDropSnapshot(string name) => $"DROP TABLE \"temp\".\"{name}\";";

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
