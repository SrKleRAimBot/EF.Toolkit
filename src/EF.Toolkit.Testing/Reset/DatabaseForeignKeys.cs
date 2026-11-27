using System.Data.Common;

namespace EFToolkit.Testing;

/// <summary>A foreign key as the database's own catalog records it: which table references which.</summary>
/// <param name="Table">The referencing table.</param>
/// <param name="ReferencedTable">The table it references.</param>
/// <param name="OutsideDatabase">
///     Whether the referencing table is in another database than the connection's (MySQL only).
/// </param>
/// <param name="TableSchema">The referencing table's schema, where the engine has schemas (SQL Server).</param>
/// <param name="ReferencedSchema">The referenced table's schema, likewise.</param>
internal sealed record DatabaseReference(
    string Table,
    string ReferencedTable,
    bool OutsideDatabase = false,
    string? TableSchema = null,
    string? ReferencedSchema = null);

/// <summary>
///     Reads foreign keys from the catalog on the engines whose reset switches enforcement off, so that
///     a table the model does not know about cannot be orphaned or cascaded into.
/// </summary>
/// <remarks>
///     SQLite and MySQL suspend checks for the whole connection while they delete, so a table outside
///     the model would lose rows or be orphaned. SQL Server keeps such a table's constraint enforced, but
///     deletes table by table, so without this it would fail only after emptying every table ahead of
///     the referenced one — a half-done reset. PostgreSQL needs no check: one <c>TRUNCATE</c> without
///     <c>CASCADE</c> fails as a whole.
/// </remarks>
internal static class DatabaseForeignKeys
{
    /// <summary>
    ///     The catalog query for <paramref name="engine" /> returning every foreign key from a table
    ///     outside <paramref name="plan" /> into one inside it, or <see langword="null" /> if the engine
    ///     needs no such check.
    /// </summary>
    /// <remarks>
    ///     The database decides which names are the same table, by its own rules, rather than a .NET
    ///     comparer approximating them: SQL Server compares object identities, SQLite compares with
    ///     <c>NOCASE</c> — ASCII-only, exactly how it matches identifiers — and MySQL compares under the
    ///     collation <c>information_schema</c> gives table names, which follows
    ///     <c>lower_case_table_names</c>. A comparer that folds more, or less, than the engine would take
    ///     an unmapped dependent for a reset table, or miss one.
    /// </remarks>
    internal static string? Query(DatabaseEngine engine, ResetPlan plan, bool mySqlNoBackslashEscapes = false)
        => engine switch
        {
            DatabaseEngine.Sqlite => $"""
                WITH "plan"("name") AS (VALUES {string.Join(", ", plan.Tables.Select(t => "(" + SqlLiteral(t.Name) + ")"))})
                SELECT m."name", f."table", 0
                FROM "main"."sqlite_master" AS m
                JOIN pragma_foreign_key_list(m."name", 'main') AS f
                WHERE m."type" = 'table'
                  AND EXISTS (SELECT 1 FROM "plan" AS p WHERE p."name" = f."table" COLLATE NOCASE)
                  AND NOT EXISTS (SELECT 1 FROM "plan" AS p WHERE p."name" = m."name" COLLATE NOCASE);
                """,
            DatabaseEngine.SqlServer => $"""
                WITH [plan](id) AS ({string.Join(" UNION ALL ", plan.Tables.Select(t => $"SELECT OBJECT_ID({SqlServerName(t)})"))})
                SELECT OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id), 0,
                       OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_SCHEMA_NAME(fk.referenced_object_id)
                FROM sys.foreign_keys AS fk
                WHERE fk.referenced_object_id IN (SELECT id FROM [plan] WHERE id IS NOT NULL)
                  AND fk.parent_object_id NOT IN (SELECT id FROM [plan] WHERE id IS NOT NULL);
                """,
            DatabaseEngine.MySql => $"""
                SELECT rc.TABLE_NAME, rc.REFERENCED_TABLE_NAME, rc.CONSTRAINT_SCHEMA <> DATABASE()
                FROM information_schema.REFERENTIAL_CONSTRAINTS AS rc
                WHERE rc.UNIQUE_CONSTRAINT_SCHEMA = DATABASE()
                  AND rc.REFERENCED_TABLE_NAME IN ({MySqlNames(plan, mySqlNoBackslashEscapes)})
                  AND NOT (rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.TABLE_NAME IN ({MySqlNames(plan, mySqlNoBackslashEscapes)}));
                """,
            _ => null,
        };

    /// <summary>
    ///     SQL Server: every reset table with an enabled row-level-security filter predicate, as rows
    ///     shaped like <see cref="DatabaseReference" /> (table, policy).
    /// </summary>
    /// <remarks>
    ///     A filter predicate applies to <c>DELETE</c> as to <c>SELECT</c> — and to the members of
    ///     db_owner too — so a reset run under one tenant's session context deletes that tenant's rows
    ///     and reports success with every other tenant's still there. Counting afterwards would not
    ///     notice: the count is filtered the same way.
    /// </remarks>
    internal static string SqlServerFilteredTables(ResetPlan plan) => $"""
        WITH [plan](id) AS ({string.Join(" UNION ALL ", plan.Tables.Select(t => $"SELECT OBJECT_ID({SqlServerName(t)})"))})
        SELECT OBJECT_NAME(pr.target_object_id), sp.name, 0
        FROM sys.security_predicates AS pr
        JOIN sys.security_policies AS sp ON sp.object_id = pr.object_id
        WHERE sp.is_enabled = 1
          AND pr.predicate_type_desc = 'FILTER'
          AND pr.target_object_id IN (SELECT id FROM [plan] WHERE id IS NOT NULL);
        """;

    /// <summary>
    ///     SQL Server: 1 when the login can see every security policy in the database, which
    ///     <see cref="SqlServerFilteredTables" /> needs to be trusted.
    /// </summary>
    /// <remarks>
    ///     Catalog views show only what the login may see. Without database-wide <c>VIEW DEFINITION</c>
    ///     (or SQL Server 2022's narrower <c>VIEW SECURITY DEFINITION</c>), a policy filtering a reset
    ///     table can be invisible, the check finds nothing, and the reset deletes only the rows the
    ///     session can see. Either permission is reported through <c>HAS_PERMS_BY_NAME</c>, which answers
    ///     <c>NULL</c> for a permission the server does not know — hence the <c>COALESCE</c>.
    /// </remarks>
    internal const string SqlServerCanSeePolicies =
        "SELECT CASE WHEN COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) = 1 "
        + "OR COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW SECURITY DEFINITION'), 0) = 1 "
        + "THEN 1 ELSE 0 END;";

    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string SqlServerName(ResetTable table)
    {
        static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

        var name = table.Schema is null ? Bracket(table.Name) : Bracket(table.Schema) + "." + Bracket(table.Name);
        return "N" + SqlLiteral(name);
    }

    /// <summary>
    ///     MySQL: one row per column of every foreign key from a table in any user database — constraint,
    ///     table, column, referenced database, table and column, whether the table is one in
    ///     <paramref name="plan" />, and the table's database — in key order.
    /// </summary>
    /// <remarks>
    ///     Every database, not just the connection's: a trigger on a reset table can write to another
    ///     one just as well. information_schema lists only the tables the user has some privilege on;
    ///     <see cref="MySqlSeesEveryTable" /> guarantees that for the connection's own database.
    /// </remarks>
    internal static string MySqlForeignKeys(ResetPlan plan, bool noBackslashEscapes) => $"""
        SELECT k.CONSTRAINT_NAME, k.TABLE_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME,
               CASE WHEN k.TABLE_SCHEMA = DATABASE() AND k.TABLE_NAME IN ({MySqlNames(plan, noBackslashEscapes)}) THEN 1 ELSE 0 END,
               k.TABLE_SCHEMA
        FROM information_schema.KEY_COLUMN_USAGE AS k
        WHERE k.TABLE_SCHEMA NOT IN ('mysql', 'sys', 'information_schema', 'performance_schema')
          AND k.REFERENCED_TABLE_NAME IS NOT NULL
        ORDER BY k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME, k.ORDINAL_POSITION;
        """;

    /// <summary>Groups the rows of <see cref="MySqlForeignKeys" /> into keys.</summary>
    internal static List<MySqlForeignKey> GroupMySqlForeignKeys(IEnumerable<string[]> rows)
        => rows
            .GroupBy(r => (Schema: r[7], Table: r[1], Constraint: r[0]))
            .Select(g => new MySqlForeignKey(
                g.Key.Table,
                [.. g.Select(r => r[2])],
                g.First()[3],
                g.First()[4],
                [.. g.Select(r => r[5])],
                InPlan: g.First()[6] != "0",
                TableSchema: g.Key.Schema))
            .ToList();

    /// <summary>
    ///     MySQL: 1 when the login holds a table privilege on the connection's whole database, and so sees
    ///     every table in it in information_schema; 0 otherwise.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A login granted only some tables does not see the others, nor their foreign keys: an
    ///         <c>ON DELETE CASCADE</c> from one would act on the reset unseen, and while checks are off
    ///         for a cycle rows there could be orphaned with nothing able to check them.
    ///     </para>
    ///     <para>
    ///         Only table privileges count. A global <c>PROCESS</c> or <c>RELOAD</c> makes no table
    ///         visible. A global table privilege counts only without <c>partial_revokes</c>: with it, a
    ///         global grant can be revoked for this one database, and that restriction is not visible
    ///         here. A database grant may use wildcards, matched by <c>LIKE</c> as MySQL matches them —
    ///         except under <c>partial_revokes</c>, where MySQL takes <c>_</c> and <c>%</c> literally and the
    ///         name must match exactly (under the catalog's collation, which follows <c>lower_case_table_names</c>).
    ///         Privileges held through a role are not listed in these views either, so such a login is
    ///         refused too, which is the safe way to be wrong.
    ///     </para>
    /// </remarks>
    internal static string MySqlSeesEveryTable(bool noBackslashEscapes)
    {
        const string grantee = "CONCAT('''', LEFT(CURRENT_USER(), LENGTH(CURRENT_USER()) - LOCATE('@', REVERSE(CURRENT_USER()))), "
            + "'''@''', SUBSTRING_INDEX(CURRENT_USER(), '@', -1), '''')";
        const string tablePrivileges = "('SELECT', 'INSERT', 'UPDATE', 'DELETE', 'CREATE', 'DROP', 'REFERENCES', 'INDEX', "
            + "'ALTER', 'CREATE VIEW', 'SHOW VIEW', 'TRIGGER')";
        var escape = noBackslashEscapes ? @"'\'" : @"'\\'";
        return "SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.SCHEMA_PRIVILEGES AS d "
            + $"WHERE d.GRANTEE = {grantee} AND d.PRIVILEGE_TYPE IN {tablePrivileges} AND (CASE WHEN @@GLOBAL.partial_revokes = 1 THEN d.TABLE_SCHEMA = DATABASE() ELSE DATABASE() LIKE d.TABLE_SCHEMA ESCAPE {escape} END)) "
            + "OR (@@GLOBAL.partial_revokes = 0 AND EXISTS (SELECT 1 FROM information_schema.USER_PRIVILEGES AS u "
            + $"WHERE u.GRANTEE = {grantee} AND u.PRIVILEGE_TYPE IN {tablePrivileges})) THEN 1 ELSE 0 END;";
    }

    /// <summary>
    ///     MySQL: the tables in <paramref name="plan" /> that are not transactional base tables — rows of
    ///     (name, what it is: <c>VIEW</c> or the storage engine).
    /// </summary>
    /// <remarks>
    ///     A view under a mapped name would be deleted through — into whatever base table it reads — and
    ///     an engine without transactions (MyISAM, MEMORY) ignores the rollback.
    /// </remarks>
    internal static string MySqlNotTransactionalTables(ResetPlan plan, bool noBackslashEscapes) => $"""
        SELECT t.TABLE_NAME, CASE WHEN t.TABLE_TYPE = 'BASE TABLE' THEN COALESCE(t.ENGINE, '') ELSE 'VIEW' END
        FROM information_schema.TABLES AS t
        LEFT JOIN information_schema.ENGINES AS e ON e.ENGINE = t.ENGINE
        WHERE t.TABLE_SCHEMA = DATABASE()
          AND t.TABLE_NAME IN ({MySqlNames(plan, noBackslashEscapes)})
          AND (t.TABLE_TYPE <> 'BASE TABLE' OR COALESCE(e.TRANSACTIONS, 'NO') <> 'YES');
        """;

    /// <summary>
    ///     SQL Server: the names in <paramref name="plan" /> that resolve to something other than a user
    ///     table — a view, say — as rows of (name, its type).
    /// </summary>
    internal static string SqlServerNotTables(ResetPlan plan) => $"""
        SELECT p.n, o.type_desc
        FROM (VALUES {string.Join(", ", plan.Tables.Select(t => $"({SqlServerName(t)})"))}) AS p(n)
        JOIN sys.objects AS o ON o.object_id = OBJECT_ID(p.n)
        WHERE o.type <> 'U';
        """;

    /// <summary>SQLite: the names in <paramref name="plan" /> that are views in <c>main</c>, as rows of (name, 'VIEW').</summary>
    internal static string SqliteViews(ResetPlan plan) => $"""
        SELECT m."name", 'VIEW' FROM "main"."sqlite_master" AS m
        WHERE m."type" = 'view' AND m."name" COLLATE NOCASE IN ({string.Join(", ", plan.Tables.Select(t => SqlLiteral(t.Name)))});
        """;

    // A backslash escapes in a MySQL string literal unless the session's sql_mode has
    // NO_BACKSLASH_ESCAPES, where it is just a backslash: doubling it there would name another table.
    private static string MySqlNames(ResetPlan plan, bool noBackslashEscapes)
        => string.Join(", ", plan.Tables.Select(t => SqlLiteral(
            noBackslashEscapes ? t.Name : t.Name.Replace("\\", "\\\\", StringComparison.Ordinal))));

    /// <summary>
    ///     SQL Server: the schema each unqualified table in <paramref name="plan" /> resolves to, as rows
    ///     shaped like <see cref="DatabaseReference" /> — the name, unused, unused, and the schema.
    /// </summary>
    /// <remarks>
    ///     <c>OBJECT_ID</c> resolves an unqualified name the way the reset's <c>DELETE</c> would — the
    ///     login's default schema first, then <c>dbo</c> — so the foreign-key check and the statements
    ///     that follow are guaranteed to be about the same table.
    /// </remarks>
    internal static string ResolveSchemas(ResetPlan plan)
    {
        var unqualified = plan.Tables.Where(t => t.Schema is null).Select(t => t.Name).Distinct().ToList();
        if (unqualified.Count == 0)
        {
            return "SELECT N'' AS n, N'' AS r, 0 AS o, N'' AS s WHERE 1 = 0;";
        }

        return string.Join("\nUNION ALL\n", unqualified.Select(name =>
        {
            var literal = "N'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
            var quoted = "N'[" + name.Replace("]", "]]", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "]'";
            return $"SELECT {literal}, N'', 0, COALESCE(OBJECT_SCHEMA_NAME(OBJECT_ID({quoted})), N'')";
        })) + ";";
    }

    /// <summary>
    ///     PostgreSQL: every table a <c>TRUNCATE</c> of the plan would also empty without being asked —
    ///     rows of (descendant, plan table).
    /// </summary>
    /// <remarks>
    ///     <c>TRUNCATE</c> without <c>ONLY</c> empties every descendant. For a partitioned table that is
    ///     the point — its partitions hold its rows — unless a partition is one the reset was told to
    ///     keep — whether the model maps it or it was only named in <c>ExcludeTable</c>. For ordinary
    ///     inheritance it is not: an <c>INHERITS</c> child is a table of its own. Names
    ///     go through <c>to_regclass</c>, resolving exactly as the <c>TRUNCATE</c> will; a table that does
    ///     not exist resolves to nothing rather than failing here.
    /// </remarks>
    /// <summary>
    ///     PostgreSQL: every reset table — rows of (its index in the plan, whether row-level security applies
    ///     to this session on it, whether it or anything TRUNCATE reaches through it has a user
    ///     <c>TRUNCATE</c> trigger).
    /// </summary>
    /// <remarks>
    ///     TRUNCATE empties a table's inheritance children and partitions with it and fires their triggers
    ///     too, so the search walks the same tree. pg_trigger is readable by every role, so no such trigger
    ///     is hidden. Row-level security would hide rows from the emptiness check that follows, which then
    ///     could not be trusted.
    /// </remarks>
    internal static string PostgreSqlTruncateTriggers(ResetPlan plan)
    {
        var classes = string.Join(", ", plan.Tables.Select(t =>
        {
            var quoted = (t.Schema is null ? "" : PgQuote(t.Schema) + ".") + PgQuote(t.Name);
            return "pg_catalog.to_regclass(" + PgLiteral(quoted) + ")";
        }));
        return $"""
            WITH RECURSIVE
                reset(r, n) AS (SELECT t.r, t.n FROM pg_catalog.unnest(ARRAY[{classes}]::pg_catalog.regclass[]) WITH ORDINALITY AS t(r, n) WHERE t.r IS NOT NULL),
                tree(n, rel) AS (
                    SELECT n, r FROM reset
                    UNION ALL
                    SELECT tree.n, i.inhrelid::pg_catalog.regclass FROM tree JOIN pg_catalog.pg_inherits AS i ON i.inhparent = tree.rel)
            SELECT (reset.n - 1)::pg_catalog.int4,
                   CASE WHEN pg_catalog.row_security_active(reset.r) THEN 1 ELSE 0 END,
                   CASE WHEN EXISTS (SELECT 1 FROM tree JOIN pg_catalog.pg_trigger AS g ON g.tgrelid = tree.rel
                                     WHERE tree.n = reset.n AND NOT g.tgisinternal AND (g.tgtype & 32) <> 0) THEN 1 ELSE 0 END
            FROM reset;
            """;
    }

    private static string PgQuote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // A string literal that reads the same whatever standard_conforming_strings says: with a backslash in
    // it, an escape string with the backslash doubled. The catalog functions and types around these
    // literals are named with pg_catalog, so a function of the same name earlier on the search_path
    // cannot answer in their place.
    private static string PgLiteral(string value)
    {
        var doubled = value.Replace("'", "''", StringComparison.Ordinal);
        return value.Contains('\\', StringComparison.Ordinal)
            ? "E'" + doubled.Replace("\\", "\\\\", StringComparison.Ordinal) + "'"
            : "'" + doubled + "'";
    }

    internal static string PostgreSqlDescendants(ResetPlan plan)
    {
        static string RegClasses(IEnumerable<ResetTable> tables)
            => string.Join(", ", tables.Select(t =>
            {
                var quoted = (t.Schema is null ? "" : Quote(t.Schema) + ".") + Quote(t.Name);
                return "pg_catalog.to_regclass(" + PgLiteral(quoted) + ")";
            }));

        static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        static string Literal(string? value)
            => value is null ? "NULL::pg_catalog.text" : PgLiteral(value) + "::pg_catalog.text";

        // ExcludeTable names, matched the way the reset matches them: name exactly, schema exactly
        // unless none was given.
        var excludedNames = plan.ExcludedNames.Count == 0
            ? "SELECT NULL::pg_catalog.text, NULL::pg_catalog.text WHERE false"
            : "VALUES " + string.Join(", ", plan.ExcludedNames.Select(e => $"({Literal(e.Name)}, {Literal(e.Schema)})"));

        return $"""
            WITH RECURSIVE
                reset(oid) AS (SELECT r FROM pg_catalog.unnest(ARRAY[{RegClasses(plan.Tables)}]::pg_catalog.regclass[]) AS r WHERE r IS NOT NULL),
                kept(oid) AS (SELECT k FROM pg_catalog.unnest(ARRAY[{RegClasses(plan.Retained)}]::pg_catalog.regclass[]) AS k WHERE k IS NOT NULL),
                excluded(name, schema) AS ({excludedNames}),
                tree(root, rel) AS (
                    SELECT oid, oid FROM reset
                    UNION ALL
                    SELECT tree.root, i.inhrelid::pg_catalog.regclass FROM tree JOIN pg_catalog.pg_inherits AS i ON i.inhparent = tree.rel)
            SELECT tree.rel::pg_catalog.text, tree.root::pg_catalog.text, 0
            FROM tree
            JOIN pg_catalog.pg_class AS root ON root.oid = tree.root
            JOIN pg_catalog.pg_class AS rel ON rel.oid = tree.rel
            JOIN pg_catalog.pg_namespace AS ns ON ns.oid = rel.relnamespace
            WHERE tree.rel <> tree.root
              AND tree.rel NOT IN (SELECT oid FROM reset)
              AND (root.relkind <> 'p'
                   OR tree.rel IN (SELECT oid FROM kept)
                   OR EXISTS (SELECT 1 FROM excluded AS e
                              WHERE e.name = rel.relname AND (e.schema IS NULL OR e.schema = ns.nspname)))
            -- A kept table that is also a reset one: two mappings naming the same table differently
            -- (Settings, and public.Settings) are one table to the database. Named twice, to tell it apart.
            UNION ALL
            SELECT k.oid::pg_catalog.text, k.oid::pg_catalog.text, 0 FROM kept AS k WHERE k.oid IN (SELECT oid FROM reset);
            """;
    }

    internal static DatabaseReference Read(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) != 0,
            reader.FieldCount > 3 ? reader.GetString(3) : null,
            reader.FieldCount > 4 ? reader.GetString(4) : null);
}
