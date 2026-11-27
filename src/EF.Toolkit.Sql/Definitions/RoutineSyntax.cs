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
    ///     Quoted, in its folded form. That is the name a bare spelling would have had, so a body that
    ///     refers to its parameters unquoted still finds them (TenantId folds to tenantid). And a key word
    ///     cannot be written bare as a parameter — not only the reserved ones: IN timestamp integer reads
    ///     timestamp as the type, and timestamp => 1 is a syntax error in a call. Quoted, any name works;
    ///     a body has to quote a key-word name to refer to it, as it always must.
    /// </remarks>
    public static string PostgreSqlParameterName(string name)
        => SqlSyntax.Quote(SqlDialect.PostgreSql, name.ToLowerInvariant());

    /// <summary>SQL Server's procedure parameter list, which is not parenthesised.</summary>
    public static string SqlServerProcedureParameters(IReadOnlyList<SqlParameterDefinition> parameters)
        => parameters.Count == 0
            ? ""
            : "\n" + string.Join(",\n", parameters.Select(p =>
                $"    @{p.Name} {p.StoreType}" + (p.Direction == SqlParameterDirection.In ? "" : " OUTPUT")));

    /// <summary>SQL Server's function parameter list, which is parenthesised.</summary>
    public static string SqlServerFunctionParameters(IReadOnlyList<SqlParameterDefinition> parameters)
        => string.Join(", ", parameters.Select(p => $"@{p.Name} {p.StoreType}"));

    /// <summary>A PostgreSQL routine's argument signature, as <c>DROP</c> takes it to name one overload.</summary>
    /// <remarks>
    ///     A function is identified by its input types alone. A procedure's <c>OUT</c> arguments are part of
    ///     its identity too, so for one every argument is listed with its mode, which PostgreSQL accepts for
    ///     both.
    /// </remarks>
    public static string PostgreSqlSignature(IReadOnlyList<SqlParameterDefinition> parameters, bool withDirection)
        => string.Join(", ", parameters.Select(p => withDirection ? $"{Direction(p.Direction)} {p.StoreType}" : p.StoreType));

    private static string Direction(SqlParameterDirection direction)
        => direction switch
        {
            SqlParameterDirection.In => "IN",
            SqlParameterDirection.Out => "OUT",
            SqlParameterDirection.InOut => "INOUT",
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        };
}
