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
            SqlDialect.MySql => ($"CALL {target}({Positional(arguments)})", values),
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

    private static string Target(SqlDialect dialect, string name, string? schema)
        => SqlSyntax.EscapeFormat(SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema))));

    private static object?[] Values(IReadOnlyList<(string Name, object? Value)> arguments)
        => [.. arguments.Select(a => a.Value)];

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
