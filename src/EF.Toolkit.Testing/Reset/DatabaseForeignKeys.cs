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
    internal static string? Query(DatabaseEngine engine, ResetPlan plan)
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
                  AND rc.REFERENCED_TABLE_NAME IN ({MySqlNames(plan)})
                  AND NOT (rc.CONSTRAINT_SCHEMA = DATABASE() AND rc.TABLE_NAME IN ({MySqlNames(plan)}));
                """,
            _ => null,
        };

    private static string SqlLiteral(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string SqlServerName(ResetTable table)
    {
        static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

        var name = table.Schema is null ? Bracket(table.Name) : Bracket(table.Schema) + "." + Bracket(table.Name);
        return "N" + SqlLiteral(name);
    }

    private static string MySqlNames(ResetPlan plan)
        => string.Join(", ", plan.Tables.Select(t => SqlLiteral(t.Name.Replace("\\", "\\\\", StringComparison.Ordinal))));

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
    internal static string PostgreSqlDescendants(ResetPlan plan)
    {
        static string RegClasses(IEnumerable<ResetTable> tables)
            => string.Join(", ", tables.Select(t =>
            {
                var quoted = (t.Schema is null ? "" : Quote(t.Schema) + ".") + Quote(t.Name);
                return "to_regclass('" + quoted.Replace("'", "''", StringComparison.Ordinal) + "')";
            }));

        static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        static string Literal(string? value)
            => value is null ? "NULL::text" : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'::text";

        // ExcludeTable names, matched the way the reset matches them: name exactly, schema exactly
        // unless none was given.
        var excludedNames = plan.ExcludedNames.Count == 0
            ? "SELECT NULL::text, NULL::text WHERE false"
            : "VALUES " + string.Join(", ", plan.ExcludedNames.Select(e => $"({Literal(e.Name)}, {Literal(e.Schema)})"));

        return $"""
            WITH RECURSIVE
                reset(oid) AS (SELECT r FROM unnest(ARRAY[{RegClasses(plan.Tables)}]::regclass[]) AS r WHERE r IS NOT NULL),
                kept(oid) AS (SELECT k FROM unnest(ARRAY[{RegClasses(plan.Retained)}]::regclass[]) AS k WHERE k IS NOT NULL),
                excluded(name, schema) AS ({excludedNames}),
                tree(root, rel) AS (
                    SELECT oid, oid FROM reset
                    UNION ALL
                    SELECT tree.root, i.inhrelid::regclass FROM tree JOIN pg_catalog.pg_inherits AS i ON i.inhparent = tree.rel)
            SELECT tree.rel::text, tree.root::text, 0
            FROM tree
            JOIN pg_catalog.pg_class AS root ON root.oid = tree.root
            JOIN pg_catalog.pg_class AS rel ON rel.oid = tree.rel
            JOIN pg_catalog.pg_namespace AS ns ON ns.oid = rel.relnamespace
            WHERE tree.rel <> tree.root
              AND tree.rel NOT IN (SELECT oid FROM reset)
              AND (root.relkind <> 'p'
                   OR tree.rel IN (SELECT oid FROM kept)
                   OR EXISTS (SELECT 1 FROM excluded AS e
                              WHERE e.name = rel.relname AND (e.schema IS NULL OR e.schema = ns.nspname)));
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
