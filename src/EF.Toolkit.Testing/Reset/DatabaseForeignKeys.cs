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
    /// <summary>The catalog query for <paramref name="engine" />, or <see langword="null" /> if it needs none.</summary>
    internal static string? Query(DatabaseEngine engine)
        => engine switch
        {
            DatabaseEngine.Sqlite => """
                SELECT m."name", f."table", 0
                FROM "sqlite_master" AS m
                JOIN pragma_foreign_key_list(m."name") AS f
                WHERE m."type" = 'table';
                """,
            DatabaseEngine.SqlServer => """
                SELECT OBJECT_NAME(fk.parent_object_id), OBJECT_NAME(fk.referenced_object_id), 0,
                       OBJECT_SCHEMA_NAME(fk.parent_object_id), OBJECT_SCHEMA_NAME(fk.referenced_object_id)
                FROM sys.foreign_keys AS fk;
                """,
            DatabaseEngine.MySql => """
                SELECT TABLE_NAME, REFERENCED_TABLE_NAME, CONSTRAINT_SCHEMA <> DATABASE()
                FROM information_schema.REFERENTIAL_CONSTRAINTS
                WHERE UNIQUE_CONSTRAINT_SCHEMA = DATABASE();
                """,
            _ => null,
        };

    /// <summary>The schema of a model table that names none, where the catalog reports schemas.</summary>
    /// <remarks>
    ///     <c>dbo</c> is SQL Server's default for the login EF connects as unless the model says
    ///     otherwise — and a model with <c>HasDefaultSchema</c> gives every table an explicit schema.
    /// </remarks>
    internal static string? DefaultSchema(DatabaseEngine engine)
        => engine == DatabaseEngine.SqlServer ? "dbo" : null;

    internal static DatabaseReference Read(DbDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            Convert.ToInt64(reader.GetValue(2), System.Globalization.CultureInfo.InvariantCulture) != 0,
            reader.FieldCount > 3 ? reader.GetString(3) : null,
            reader.FieldCount > 4 ? reader.GetString(4) : null);
}
