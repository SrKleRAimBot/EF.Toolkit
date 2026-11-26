using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Security;

/// <summary>Turns PostgreSQL row-level security on for a table — and off again when dropped.</summary>
/// <remarks>
///     <para>
///         Enabling RLS on a table with no policies hides every row from everyone it applies to, so
///         create the policies in the same migration.
///     </para>
///     <para>
///         <c>FORCE</c> matters more than it looks. Without it the table's <em>owner</em> bypasses every
///         policy, and the owner is usually the role the application and its migrations connect as —
///         so a policy that is correct in every respect can still filter nothing. Superusers and roles
///         with <c>BYPASSRLS</c> bypass RLS regardless.
///     </para>
///     <para>
///         SQL Server enables row-level security per security policy rather than per table; see
///         <see cref="SqlServerSecurityPolicy" />.
///     </para>
/// </remarks>
public sealed class RowLevelSecurityTable : IDatabaseObject
{
    internal const string Feature = "Row-level security (ALTER TABLE ... ENABLE ROW LEVEL SECURITY)";

    /// <summary>Initializes a new instance of the <see cref="RowLevelSecurityTable" /> class.</summary>
    /// <param name="table">The table name.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="force">Apply the policies to the table's owner too.</param>
    public RowLevelSecurityTable(string table, string? schema = null, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        Table = table;
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        Force = force;
    }

    /// <summary>The table name.</summary>
    public string Table { get; }

    /// <summary>The schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>Whether the policies also apply to the table's owner.</summary>
    public bool Force { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        RequirePostgreSql(dialect, Feature);

        var target = SqlSyntax.Qualify(dialect, Table, Schema);
        return Force
            ? [$"ALTER TABLE {target} ENABLE ROW LEVEL SECURITY", $"ALTER TABLE {target} FORCE ROW LEVEL SECURITY"]
            : [$"ALTER TABLE {target} ENABLE ROW LEVEL SECURITY"];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect) => DropSql(dialect, Table, Schema);

    /// <remarks>
    ///     <c>NO FORCE</c> is always emitted, whether or not this definition forced: <c>DISABLE</c>
    ///     alone leaves the force flag set, and it would come back into effect the next time someone
    ///     enabled RLS on the table.
    /// </remarks>
    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string table, string? schema)
    {
        RequirePostgreSql(dialect, Feature);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        var target = SqlSyntax.Qualify(dialect, table, SqlSyntax.OptionalName(schema, nameof(schema)));
        return [$"ALTER TABLE {target} NO FORCE ROW LEVEL SECURITY", $"ALTER TABLE {target} DISABLE ROW LEVEL SECURITY"];
    }

    internal static void RequirePostgreSql(SqlDialect dialect, string feature)
    {
        if (dialect == SqlDialect.PostgreSql)
        {
            return;
        }

        throw SqlFeatureNotSupportedException.For(
            feature,
            dialect,
            dialect switch
            {
                SqlDialect.SqlServer =>
                    "SQL Server's row-level security is a security policy over a predicate function: use "
                    + "SqlServerSecurityPolicy, or TenantIsolationPolicy for the common tenant case.",
                _ => $"{SqlSyntax.EngineName(dialect)} has no row-level security; filter in the "
                    + "application, for example with a global query filter.",
            });
    }
}
