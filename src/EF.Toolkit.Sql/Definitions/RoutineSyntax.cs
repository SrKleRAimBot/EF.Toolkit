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
    public static string ParameterList(IReadOnlyList<SqlParameterDefinition> parameters, bool withDirection)
        => string.Join(", ", parameters.Select(p => withDirection
            ? $"{Direction(p.Direction)} {p.Name} {p.StoreType}"
            : $"{p.Name} {p.StoreType}"));

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
