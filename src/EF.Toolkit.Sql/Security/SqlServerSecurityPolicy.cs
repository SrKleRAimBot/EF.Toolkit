using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Security;

/// <summary>
///     SQL Server row-level security: an inline predicate function and a security policy that binds it
///     to a table as a filter and block predicate.
/// </summary>
/// <remarks>
///     <para>
///         The function is created <c>WITH SCHEMABINDING</c>, which SQL Server requires of a security
///         predicate, and returns <c>SELECT 1 AS allowed WHERE &lt;predicate&gt;</c> — a row is visible
///         or writable exactly when the predicate is true for it. A predicate that evaluates to
///         <c>NULL</c>, as one comparing against an unset <c>SESSION_CONTEXT</c> does, rejects the row:
///         the policy fails closed.
///     </para>
///     <para>
///         A schema-bound function cannot be altered while a security policy refers to it, so creating
///         drops the policy first, replaces the function and creates the policy again; dropping removes
///         the policy before the function.
///     </para>
///     <para>
///         Members of <c>db_owner</c> and <c>sysadmin</c> are subject to the policy like everyone else,
///         unlike PostgreSQL's owners and superusers — but they can also alter or drop it.
///     </para>
/// </remarks>
public sealed class SqlServerSecurityPolicy : IDatabaseObject
{
    internal const string Feature = "Security policies (CREATE SECURITY POLICY)";
    private const string DefaultSchema = "dbo";

    /// <summary>Initializes a new instance of the <see cref="SqlServerSecurityPolicy" /> class.</summary>
    /// <param name="name">The security policy name.</param>
    /// <param name="table">The table the policy protects.</param>
    /// <param name="functionName">The predicate function's name.</param>
    /// <param name="parameters">The function's parameters, each bound to a column of the table.</param>
    /// <param name="predicate">
    ///     The condition, in T-SQL, under which a row is allowed — written in terms of the parameters,
    ///     for example <c>@TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS int)</c>.
    /// </param>
    /// <param name="schema">The table's schema. <see langword="null" /> means <c>dbo</c>.</param>
    /// <param name="securitySchema">
    ///     The schema for the function and the policy. <see langword="null" /> means the table's schema.
    ///     Microsoft recommends a dedicated schema that application users cannot alter.
    /// </param>
    /// <param name="filter">Add a filter predicate, hiding rejected rows from reads, updates and deletes.</param>
    /// <param name="block">The writes to check with block predicates.</param>
    public SqlServerSecurityPolicy(
        string name,
        string table,
        string functionName,
        IEnumerable<SqlServerPredicateParameter> parameters,
        string predicate,
        string? schema = null,
        string? securitySchema = null,
        bool filter = true,
        SqlServerBlockOperations block = SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(functionName);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);

        var parameterList = parameters.ToArray();
        if (parameterList.Length == 0 || parameterList.Any(p => p is null))
        {
            throw new ArgumentException(
                "A predicate function needs at least one parameter bound to a column, and none can be null.",
                nameof(parameters));
        }

        if (parameterList.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameterList.Length)
        {
            throw new ArgumentException("Predicate parameters must have distinct names.", nameof(parameters));
        }

        if ((block & ~(SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate
                | SqlServerBlockOperations.BeforeUpdate | SqlServerBlockOperations.BeforeDelete)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(block), block, "Not a combination of defined block operations.");
        }

        if (!filter && block == SqlServerBlockOperations.None)
        {
            throw new ArgumentException(
                "A security policy with neither a filter nor a block predicate would protect nothing.",
                nameof(block));
        }

        Name = name;
        Table = table;
        FunctionName = functionName;
        Parameters = parameterList;
        Predicate = predicate.Trim();
        Schema = SqlSyntax.OptionalName(schema, nameof(schema)) ?? DefaultSchema;
        SecuritySchema = SqlSyntax.OptionalName(securitySchema, nameof(securitySchema)) ?? Schema;
        Filter = filter;
        Block = block;
    }

    /// <summary>The security policy name.</summary>
    public string Name { get; }

    /// <summary>The protected table.</summary>
    public string Table { get; }

    /// <summary>The predicate function's name.</summary>
    public string FunctionName { get; }

    /// <summary>The function's parameters and the columns bound to them.</summary>
    public IReadOnlyList<SqlServerPredicateParameter> Parameters { get; }

    /// <summary>The condition under which a row is allowed.</summary>
    public string Predicate { get; }

    /// <summary>The table's schema.</summary>
    /// <remarks>
    ///     Never <see langword="null" />: a security policy must name its function and table with two
    ///     parts, so the default schema is spelled out as <c>dbo</c>.
    /// </remarks>
    public string Schema { get; }

    /// <summary>The schema of the function and the policy.</summary>
    public string SecuritySchema { get; }

    /// <summary>Whether a filter predicate is added.</summary>
    public bool Filter { get; }

    /// <summary>The writes checked with block predicates.</summary>
    public SqlServerBlockOperations Block { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        RequireSqlServer(dialect);

        var function = SqlSyntax.Qualify(dialect, FunctionName, SecuritySchema);
        var table = SqlSyntax.Qualify(dialect, Table, Schema);
        var parameterList = string.Join(", ", Parameters.Select(p => $"@{p.Name} {p.StoreType}"));
        var call = $"{function}({string.Join(", ", Parameters.Select(p => SqlSyntax.Quote(dialect, p.Column)))})";

        var predicates = new List<string>();
        if (Filter)
        {
            predicates.Add($"    ADD FILTER PREDICATE {call} ON {table}");
        }

        foreach (var (operation, clause) in BlockClauses)
        {
            if (Block.HasFlag(operation))
            {
                predicates.Add($"    ADD BLOCK PREDICATE {call} ON {table} {clause}");
            }
        }

        return
        [
            DropPolicySql(dialect),
            $"CREATE OR ALTER FUNCTION {function}({parameterList})\n"
                + "RETURNS TABLE\n"
                + "WITH SCHEMABINDING\n"
                + "AS\n"
                + $"RETURN SELECT 1 AS allowed WHERE {Predicate}",
            $"CREATE SECURITY POLICY {SqlSyntax.Qualify(dialect, Name, SecuritySchema)}\n"
                + string.Join(",\n", predicates) + "\n"
                + "WITH (STATE = ON)",
        ];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect)
    {
        RequireSqlServer(dialect);
        return
        [
            DropPolicySql(dialect),
            $"DROP FUNCTION IF EXISTS {SqlSyntax.Qualify(dialect, FunctionName, SecuritySchema)}",
        ];
    }

    private static readonly (SqlServerBlockOperations Operation, string Clause)[] BlockClauses =
    [
        (SqlServerBlockOperations.AfterInsert, "AFTER INSERT"),
        (SqlServerBlockOperations.AfterUpdate, "AFTER UPDATE"),
        (SqlServerBlockOperations.BeforeUpdate, "BEFORE UPDATE"),
        (SqlServerBlockOperations.BeforeDelete, "BEFORE DELETE"),
    ];

    private string DropPolicySql(SqlDialect dialect)
        => $"DROP SECURITY POLICY IF EXISTS {SqlSyntax.Qualify(dialect, Name, SecuritySchema)}";

    private static void RequireSqlServer(SqlDialect dialect)
    {
        if (dialect == SqlDialect.SqlServer)
        {
            return;
        }

        throw SqlFeatureNotSupportedException.For(
            Feature,
            dialect,
            dialect == SqlDialect.PostgreSql
                ? "Use RowLevelSecurityTable and RowLevelSecurityPolicy, or TenantIsolationPolicy for the common tenant case."
                : $"{SqlSyntax.EngineName(dialect)} has no row-level security; filter in the application, "
                    + "for example with a global query filter.");
    }
}
