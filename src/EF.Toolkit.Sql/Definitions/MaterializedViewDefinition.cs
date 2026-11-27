using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>A PostgreSQL materialized view.</summary>
/// <remarks>
///     <para>
///         PostgreSQL has no <c>CREATE OR REPLACE MATERIALIZED VIEW</c>, so creating one drops any
///         existing view of the same name first. Indexes on the materialized view go with it and must
///         be created again after it — including the unique index that
///         <c>RefreshMaterializedViewAsync(concurrently: true)</c> requires. The drop is not
///         <c>CASCADE</c>: if another view depends on this one, the statement fails rather than
///         silently taking the dependent view with it.
///     </para>
///     <para>
///         Only PostgreSQL has materialized views. SQL Server's indexed views behave differently enough
///         — maintained on every write, with a long list of restrictions — that rendering one under
///         this name would mislead, so every other engine throws.
///     </para>
/// </remarks>
public sealed class MaterializedViewDefinition : IDatabaseObject
{
    internal const string Feature = "Materialized views";

    /// <summary>Initializes a new instance of the <see cref="MaterializedViewDefinition" /> class.</summary>
    /// <param name="name">The materialized view name.</param>
    /// <param name="sql">The view's query. Semicolons at its very end are dropped; one followed by a comment is not.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="withNoData">
    ///     Create the view unpopulated (<c>WITH NO DATA</c>), deferring the cost of the query to the
    ///     first refresh. Querying it before then is an error.
    /// </param>
    public MaterializedViewDefinition(string name, string sql, string? schema = null, bool withNoData = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        Name = name;
        Sql = sql;
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        WithNoData = withNoData;
    }

    /// <summary>The materialized view name.</summary>
    public string Name { get; }

    /// <summary>The view's query.</summary>
    public string Sql { get; }

    /// <summary>The schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>Whether the view is created unpopulated.</summary>
    public bool WithNoData { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        RequirePostgreSql(dialect);

        var target = SqlSyntax.Qualify(dialect, Name, Schema);
        var create = $"CREATE MATERIALIZED VIEW {target} AS\n{SqlSyntax.TrimQuery(Sql)}";
        if (WithNoData)
        {
            create += "\nWITH NO DATA";
        }

        return [ReplaceDropSql(), create];
    }

    // Replacing drops the view the CREATE is about to make. With a schema that is plain. Without one,
    // DROP and CREATE resolve the bare name differently: CREATE puts it in the first schema on the
    // search_path, while DROP finds the first schema that has one — with search_path app, public and no
    // app.order_totals, an unqualified DROP would remove public's view, indexes and all. So the drop
    // names current_schema(), the schema the CREATE will use, worked out when it runs.
    private string ReplaceDropSql()
    {
        if (Schema is not null)
        {
            return DropSql(SqlDialect.PostgreSql, Name, Schema)[0];
        }

        var name = SqlSyntax.Literal(SqlDialect.PostgreSql, Name);
        var body = $"""
            BEGIN
                IF current_schema() IS NOT NULL THEN
                    EXECUTE format('DROP MATERIALIZED VIEW IF EXISTS %I.%I', current_schema(), {name});
                END IF;
            END
            """;
        return $"DO {SqlSyntax.DollarQuote(body)}";
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect) => DropSql(dialect, Name, Schema);

    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string name, string? schema)
    {
        RequirePostgreSql(dialect);
        return [$"DROP MATERIALIZED VIEW IF EXISTS {SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema)))}"];
    }

    internal static string RefreshSql(SqlDialect dialect, string name, string? schema, bool concurrently)
    {
        RequirePostgreSql(dialect);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var target = SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema)));
        return concurrently
            ? $"REFRESH MATERIALIZED VIEW CONCURRENTLY {target}"
            : $"REFRESH MATERIALIZED VIEW {target}";
    }

    private static void RequirePostgreSql(SqlDialect dialect)
    {
        if (dialect != SqlDialect.PostgreSql)
        {
            throw SqlFeatureNotSupportedException.For(
                Feature,
                dialect,
                dialect == SqlDialect.SqlServer
                    ? "An indexed view (a schema-bound view with a unique clustered index) is the nearest "
                        + "equivalent, but it is maintained on every write rather than refreshed; create "
                        + "it with ViewDefinition plus migrationBuilder.Sql for the index."
                    : "Use a ViewDefinition, or a table populated by your own refresh job.");
        }
    }
}
