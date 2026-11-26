using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Security;

/// <summary>A PostgreSQL row-level security policy.</summary>
/// <remarks>
///     <para>
///         A policy has no effect until row-level security is enabled on its table; pair it with a
///         <see cref="RowLevelSecurityTable" />.
///     </para>
///     <para>
///         PostgreSQL has no <c>CREATE OR REPLACE POLICY</c>, so creating one drops any policy of the
///         same name on the table first. Inside a migration both statements share the migration's
///         transaction, so there is no moment at which the table is unprotected.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     new RowLevelSecurityPolicy(
///         name: "orders_owner",
///         table: "Orders",
///         usingExpression: """ "OwnerId" = current_user """,
///         command: PolicyCommand.Select);
///     </code>
/// </example>
public sealed class RowLevelSecurityPolicy : IDatabaseObject
{
    internal const string Feature = "Row-level security policies (CREATE POLICY)";

    /// <summary>Initializes a new instance of the <see cref="RowLevelSecurityPolicy" /> class.</summary>
    /// <param name="name">The policy name, unique per table.</param>
    /// <param name="table">The table the policy applies to.</param>
    /// <param name="usingExpression">
    ///     The <c>USING</c> expression: which existing rows are visible to, or can be updated or deleted
    ///     by, the command. Not allowed for <see cref="PolicyCommand.Insert" />.
    /// </param>
    /// <param name="withCheckExpression">
    ///     The <c>WITH CHECK</c> expression: which new or updated rows may be written. Not allowed for
    ///     <see cref="PolicyCommand.Select" /> or <see cref="PolicyCommand.Delete" />.
    /// </param>
    /// <param name="schema">The table's schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="command">The commands the policy applies to.</param>
    /// <param name="kind">How the policy combines with the table's other policies.</param>
    /// <param name="roles">
    ///     The roles the policy applies to. <see langword="null" /> or empty for <c>PUBLIC</c> (everyone).
    ///     <c>PUBLIC</c>, <c>CURRENT_USER</c>, <c>CURRENT_ROLE</c> and <c>SESSION_USER</c> are emitted as
    ///     keywords; every other name is quoted.
    /// </param>
    public RowLevelSecurityPolicy(
        string name,
        string table,
        string? usingExpression,
        string? withCheckExpression = null,
        string? schema = null,
        PolicyCommand command = PolicyCommand.All,
        PolicyKind kind = PolicyKind.Permissive,
        IEnumerable<string>? roles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        if (!Enum.IsDefined(command))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Not a defined command.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined policy kind.");
        }

        usingExpression = Blank(usingExpression);
        withCheckExpression = Blank(withCheckExpression);

        // PostgreSQL rejects these combinations at CREATE POLICY time; refusing them here moves the
        // failure from the migration run to the line that wrote the definition.
        switch (command)
        {
            case PolicyCommand.Insert when usingExpression is not null:
                throw new ArgumentException("An INSERT policy cannot have a USING expression; use WITH CHECK.", nameof(usingExpression));
            case PolicyCommand.Insert when withCheckExpression is null:
                throw new ArgumentException("An INSERT policy needs a WITH CHECK expression.", nameof(withCheckExpression));
            case PolicyCommand.Select or PolicyCommand.Delete when withCheckExpression is not null:
                throw new ArgumentException($"A {command.ToString().ToUpperInvariant()} policy cannot have a WITH CHECK expression; use USING.", nameof(withCheckExpression));
            case PolicyCommand.Select or PolicyCommand.Delete when usingExpression is null:
                throw new ArgumentException($"A {command.ToString().ToUpperInvariant()} policy needs a USING expression.", nameof(usingExpression));
            case PolicyCommand.All or PolicyCommand.Update when usingExpression is null && withCheckExpression is null:
                throw new ArgumentException("A policy needs a USING expression, a WITH CHECK expression, or both.", nameof(usingExpression));
        }

        var roleList = roles?.ToArray() ?? [];
        if (roleList.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Role names cannot be null or blank.", nameof(roles));
        }

        Name = name;
        Table = table;
        Using = usingExpression;
        WithCheck = withCheckExpression;
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        Command = command;
        Kind = kind;
        Roles = roleList;
    }

    /// <summary>The policy name.</summary>
    public string Name { get; }

    /// <summary>The table the policy applies to.</summary>
    public string Table { get; }

    /// <summary>The <c>USING</c> expression, if any.</summary>
    public string? Using { get; }

    /// <summary>The <c>WITH CHECK</c> expression, if any.</summary>
    public string? WithCheck { get; }

    /// <summary>The table's schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>The commands the policy applies to.</summary>
    public PolicyCommand Command { get; }

    /// <summary>How the policy combines with the table's other policies.</summary>
    public PolicyKind Kind { get; }

    /// <summary>The roles the policy applies to; empty for <c>PUBLIC</c>.</summary>
    public IReadOnlyList<string> Roles { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        RowLevelSecurityTable.RequirePostgreSql(dialect, Feature);

        var sql = $"CREATE POLICY {SqlSyntax.Quote(dialect, Name)} ON {SqlSyntax.Qualify(dialect, Table, Schema)}\n"
            + $"    AS {Kind.ToString().ToUpperInvariant()}\n"
            + $"    FOR {Command.ToString().ToUpperInvariant()}\n"
            + $"    TO {(Roles.Count == 0 ? "PUBLIC" : string.Join(", ", Roles.Select(Role)))}";

        if (Using is not null)
        {
            sql += $"\n    USING ({Using})";
        }

        if (WithCheck is not null)
        {
            sql += $"\n    WITH CHECK ({WithCheck})";
        }

        return [.. DropSql(dialect, Name, Table, Schema), sql];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect) => DropSql(dialect, Name, Table, Schema);

    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string name, string table, string? schema)
    {
        RowLevelSecurityTable.RequirePostgreSql(dialect, Feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        return [$"DROP POLICY IF EXISTS {SqlSyntax.Quote(dialect, name)} ON {SqlSyntax.Qualify(dialect, table, SqlSyntax.OptionalName(schema, nameof(schema)))}"];
    }

    private static string Role(string role)
        => role.ToUpperInvariant() switch
        {
            // Role specifications that are keywords: quoted, "public" would name a role called public,
            // which does not exist, rather than everyone.
            "PUBLIC" or "CURRENT_USER" or "CURRENT_ROLE" or "SESSION_USER" => role.ToUpperInvariant(),
            _ => SqlSyntax.Quote(SqlDialect.PostgreSql, role),
        };

    private static string? Blank(string? expression)
        => string.IsNullOrWhiteSpace(expression) ? null : expression.Trim();
}
