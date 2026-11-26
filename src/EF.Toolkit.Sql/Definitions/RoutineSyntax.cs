using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>The parts of routine definitions shared by procedures and functions.</summary>
internal static class RoutineSyntax
{
    public static SqlParameterDefinition[] CopyParameters(
        IEnumerable<SqlParameterDefinition>? parameters,
        bool allowOutput,
        string paramName)
    {
        // Copied so a caller mutating its list afterwards cannot change an immutable definition.
        var copy = parameters?.ToArray() ?? [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in copy)
        {
            if (parameter is null)
            {
                throw new ArgumentException("Parameters cannot contain null.", paramName);
            }

            if (!allowOutput && parameter.Direction != SqlParameterDirection.In)
            {
                throw new ArgumentException(
                    $"Function parameter '{parameter.Name}' is {parameter.Direction}. Functions return "
                    + "their result rather than writing parameters, and SQL Server and MySQL reject "
                    + "output parameters on functions outright; use a stored procedure instead.",
                    paramName);
            }

            // Case-insensitive because every engine here folds or ignores case for these names.
            if (!seen.Add(parameter.Name))
            {
                throw new ArgumentException($"Parameter '{parameter.Name}' is declared twice.", paramName);
            }
        }

        return copy;
    }

    /// <summary>A parenthesised-list body for PostgreSQL and MySQL.</summary>
    public static string ParameterList(
        IReadOnlyList<SqlParameterDefinition> parameters,
        bool withDirection,
        SqlDialect dialect = SqlDialect.MySql)
        => string.Join(", ", parameters.Select(p =>
        {
            // MySQL quotes every name: quoting changes nothing there (parameter names ignore case either
            // way, and the body's bare references still resolve), and a reserved word such as `select`
            // cannot be written bare. A refused CREATE would leave a replaced procedure dropped.
            var name = dialect == SqlDialect.PostgreSql
                ? PostgreSqlParameterName(p.Name)
                : SqlSyntax.Quote(SqlDialect.MySql, p.Name);
            return withDirection ? $"{Direction(p.Direction)} {name} {p.StoreType}" : $"{name} {p.StoreType}";
        }));

    /// <summary>A PostgreSQL routine parameter name as written in DDL and in a named-notation call.</summary>
    /// <remarks>
    ///     Bare, so it folds to lowercase like any unquoted identifier: a routine body refers to its
    ///     parameters unquoted, and quoting a mixed-case name here would leave those references pointing
    ///     at nothing. A reserved word cannot be written bare at all, so it is quoted — in its folded form,
    ///     which is the name the bare spelling would have had.
    /// </remarks>
    public static string PostgreSqlParameterName(string name)
    {
        var folded = name.ToLowerInvariant();
        return PostgreSqlReserved.Contains(folded) ? SqlSyntax.Quote(SqlDialect.PostgreSql, folded) : name;
    }

    // PostgreSQL's reserved key words: the ones it refuses as a bare parameter or argument name.
    private static readonly HashSet<string> PostgreSqlReserved = new(StringComparer.Ordinal)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both", "case",
        "cast", "check", "collate", "column", "constraint", "create", "current_catalog", "current_date",
        "current_role", "current_time", "current_timestamp", "current_user", "default", "deferrable",
        "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for", "foreign", "from",
        "grant", "group", "having", "in", "initially", "intersect", "into", "lateral", "leading", "limit",
        "localtime", "localtimestamp", "not", "null", "offset", "on", "only", "or", "order", "placing",
        "primary", "references", "returning", "select", "session_user", "some", "symmetric",
        "system_user", "table", "then", "to", "trailing", "true", "union", "unique", "user", "using",
        "variadic", "when", "where", "window", "with",
    };

    /// <summary>SQL Server's procedure parameter list, which is not parenthesised.</summary>
    public static string SqlServerProcedureParameters(IReadOnlyList<SqlParameterDefinition> parameters)
        => parameters.Count == 0
            ? ""
            : "\n" + string.Join(",\n", parameters.Select(p =>
                $"    @{p.Name} {p.StoreType}" + (p.Direction == SqlParameterDirection.In ? "" : " OUTPUT")));

    /// <summary>SQL Server's function parameter list, which is parenthesised.</summary>
    public static string SqlServerFunctionParameters(IReadOnlyList<SqlParameterDefinition> parameters)
        => string.Join(", ", parameters.Select(p => $"@{p.Name} {p.StoreType}"));

    private static string Direction(SqlParameterDirection direction)
        => direction switch
        {
            SqlParameterDirection.In => "IN",
            SqlParameterDirection.Out => "OUT",
            SqlParameterDirection.InOut => "INOUT",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
}
