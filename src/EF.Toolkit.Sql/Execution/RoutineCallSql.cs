using System.Data;
using System.Data.Common;
using System.Globalization;
using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Execution;

/// <summary>Renders calls to stored procedures and table-valued functions for EF's raw SQL.</summary>
/// <remarks>
///     Every argument becomes a <c>{n}</c> placeholder that EF binds as a parameter; no value is ever
///     written into the SQL text. Names are quoted, and braces in them escaped, because EF runs the
///     text through <c>string.Format</c>.
/// </remarks>
internal static class RoutineCallSql
{
    internal const string ProcedureCallFeature = "Calling stored procedures";
    internal const string FunctionQueryFeature = "Querying table-valued functions";

    public static (string Sql, object?[] Arguments) Procedure(SqlDialect dialect, string name, string? schema, object? args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var target = Target(dialect, name, schema);
        var arguments = RoutineArguments.From(args);
        var values = Values(arguments);

        return dialect switch
        {
            // Named notation, so arguments match by name and a procedure's defaults can be left out.
            SqlDialect.PostgreSql => ($"CALL {target}({Named(arguments)})", values),
            SqlDialect.SqlServer => ($"EXEC {target}{SqlServerNamed(arguments)}", values),

            // MySQL's CALL is positional only: arguments go in the order given.
            SqlDialect.MySql => ($"CALL {target}({Positional(RefuseMySqlOutput(arguments))})", values),
            SqlDialect.Sqlite => throw SqlFeatureNotSupportedException.For(
                ProcedureCallFeature,
                dialect,
                "SQLite has no stored procedures."),
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    public static (string Sql, object?[] Arguments) Function(SqlDialect dialect, string name, string? schema, object? args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var target = Target(dialect, name, schema);
        var arguments = RoutineArguments.From(args);
        var values = Values(arguments);

        return dialect switch
        {
            SqlDialect.PostgreSql => ($"SELECT * FROM {target}({Named(arguments)})", values),

            // SQL Server functions take arguments by position only.
            SqlDialect.SqlServer => ($"SELECT * FROM {target}({Positional(arguments)})", values),
            SqlDialect.MySql => throw FunctionDefinition.NotOnMySql(),
            SqlDialect.Sqlite => throw FunctionDefinition.NotOnSqlite(FunctionQueryFeature),
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    // The call goes out as command text, and MySQL's client libraries bind only input parameters to a text
    // command (MySqlConnector refuses the rest outright), so an OUT or INOUT parameter could never come
    // back. Refused here, before anything runs, rather than by the provider half way through.
    private static IReadOnlyList<(string Name, object? Value)> RefuseMySqlOutput(IReadOnlyList<(string Name, object? Value)> arguments)
    {
        foreach (var (name, value) in arguments)
        {
            if (value is DbParameter { Direction: not ParameterDirection.Input } parameter)
            {
                throw SqlFeatureNotSupportedException.For(
                    "Output parameters in procedure calls",
                    SqlDialect.MySql,
                    $"Argument '{name}' is an {parameter.Direction} parameter, but MySQL binds only input parameters "
                    + "to a CALL sent as text. Pass a session variable instead — ExecuteSqlRaw(\"CALL p(@total)\") "
                    + "then SELECT @total, which needs AllowUserVariables=true in the connection string — or "
                    + "return the value as a result set and read it with QueryProcedureAsync.");
            }
        }

        return arguments;
    }

    private static string Target(SqlDialect dialect, string name, string? schema)
        => SqlSyntax.EscapeFormat(SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema))));

    // EF names the parameter for the value at index i p{i}, and binds a DbParameter under the name it
    // already has (or that same p{i}, when it has none). A DbParameter named like another argument's
    // generated name, or like another DbParameter, would put two arguments under one name: refused here,
    // naming the one to rename. Compared without the prefix and ignoring case, as SQL Server does.
    private static object?[] Values(IReadOnlyList<(string Name, object? Value)> arguments)
    {
        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < arguments.Count; i++)
        {
            var given = (arguments[i].Value as DbParameter)?.ParameterName.TrimStart('@', ':', '?', '$');
            var name = string.IsNullOrEmpty(given) ? string.Create(CultureInfo.InvariantCulture, $"p{i}") : given;
            if (!bound.TryAdd(name, arguments[i].Name))
            {
                var (first, second) = (bound[name], arguments[i].Name);
                throw new ArgumentException(
                    $"Arguments '{first}' and '{second}' would both be bound as parameter '{name}': EF names an argument's "
                    + "parameter p0, p1, ... by its position, and a DbParameter keeps the name it was given. Rename the "
                    + "DbParameter, or give it no name and let EF name it.");
            }
        }

        return [.. arguments.Select(a => a.Value)];
    }

    private static string Placeholder(int index) => string.Create(CultureInfo.InvariantCulture, $"{{{index}}}");

    private static string Positional(IReadOnlyList<(string Name, object? Value)> arguments)
        => string.Join(", ", arguments.Select((_, i) => Placeholder(i)));

    private static string Named(IReadOnlyList<(string Name, object? Value)> arguments)
        // Spelled as the routine's DDL spells it, reserved words quoted (RoutineSyntax).
        => string.Join(", ", arguments.Select((a, i) => RoutineSyntax.PostgreSqlParameterName(a.Name) + " => " + Placeholder(i)));

    private static string SqlServerNamed(IReadOnlyList<(string Name, object? Value)> arguments)
        => arguments.Count == 0
            ? ""
            : " " + string.Join(", ", arguments.Select((a, i) =>
                $"@{a.Name} = {Placeholder(i)}"
                + (a.Value is DbParameter { Direction: ParameterDirection.Output or ParameterDirection.InputOutput } ? " OUTPUT" : "")));
}
