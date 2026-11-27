using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>A view, created or replaced in place.</summary>
/// <remarks>
///     PostgreSQL and MySQL use <c>CREATE OR REPLACE VIEW</c> and SQL Server <c>CREATE OR ALTER VIEW</c>,
///     so redefining a view keeps the permissions granted on it. SQLite has neither, so there the view is
///     dropped and created again — SQLite has no grants to lose.
/// </remarks>
/// <example>
///     <code>
///     var view = new ViewDefinition("ActiveCustomers",
///         """SELECT "Id", "Name" FROM "Customers" WHERE "IsActive" """);
///     </code>
/// </example>
public sealed class ViewDefinition : IDatabaseObject
{
    /// <summary>Initializes a new instance of the <see cref="ViewDefinition" /> class.</summary>
    /// <param name="name">The view name.</param>
    /// <param name="sql">The view's query, in the target engine's dialect. Semicolons at its very end are dropped; one followed by a comment is not.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    public ViewDefinition(string name, string sql, string? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Name = name;
        Sql = sql;
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
    }

    /// <summary>The view name.</summary>
    public string Name { get; }

    /// <summary>The view's query.</summary>
    public string Sql { get; }

    /// <summary>The schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        var target = SqlSyntax.Qualify(dialect, Name, Schema);
        var query = SqlSyntax.TrimQuery(Sql);

        return dialect switch
        {
            SqlDialect.PostgreSql or SqlDialect.MySql => [$"CREATE OR REPLACE VIEW {target} AS\n{query}"],
            SqlDialect.SqlServer => [$"CREATE OR ALTER VIEW {target} AS\n{query}"],
            SqlDialect.Sqlite => [.. DropSql(dialect, Name, Schema), $"CREATE VIEW {target} AS\n{query}"],
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect) => DropSql(dialect, Name, Schema);

    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string name, string? schema)
        => [$"DROP VIEW IF EXISTS {SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema)))}"];
}
