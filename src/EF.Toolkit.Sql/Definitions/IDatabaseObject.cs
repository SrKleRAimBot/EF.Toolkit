namespace EFToolkit.Sql.Definitions;

/// <summary>A database object that can render its own create and drop SQL for any dialect.</summary>
/// <remarks>
///     <para>
///         The same definition serves a migration (<c>migrationBuilder.CreateView(...)</c>) and a
///         database that never runs migrations
///         (<c>context.Database.CreateDatabaseObjectsAsync(...)</c> after <c>EnsureCreated</c>), so a
///         test database cannot drift from the production schema by having its views written twice.
///     </para>
///     <para>
///         Each method returns separate statements rather than one script. Several engines require a
///         statement to stand alone — SQL Server's <c>CREATE VIEW</c> must be the first statement in
///         its batch, MySQL has no batch separator without the client-side <c>DELIMITER</c> — so the
///         statements are executed one at a time.
///     </para>
/// </remarks>
public interface IDatabaseObject
{
    /// <summary>The statements that create the object, or replace it if it already exists.</summary>
    /// <param name="dialect">The engine to render for.</param>
    /// <returns>One or more statements, to be executed in order.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The object has no faithful rendering on this engine.</exception>
    IReadOnlyList<string> GetCreateSql(SqlDialect dialect);

    /// <summary>The statements that drop the object. Dropping an object that does not exist is not an error.</summary>
    /// <param name="dialect">The engine to render for.</param>
    /// <returns>One or more statements, to be executed in order.</returns>
    /// <exception cref="SqlFeatureNotSupportedException">The object has no faithful rendering on this engine.</exception>
    IReadOnlyList<string> GetDropSql(SqlDialect dialect);
}
