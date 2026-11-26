using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>A stored procedure, created or replaced in place.</summary>
/// <remarks>
///     <para>
///         The body is the engine's own routine body, verbatim: on PostgreSQL what goes between the
///         dollar quotes (a <c>plpgsql</c> <c>BEGIN ... END;</c> block by default), on SQL Server what
///         follows <c>AS</c>, on MySQL the statement after the parameter list — usually
///         <c>BEGIN ... END</c>. Procedure bodies are not portable between engines, so a portable
///         definition would have to invent a language; this one only takes care of the parts around
///         the body that differ.
///     </para>
///     <para>
///         PostgreSQL and SQL Server replace the procedure in place, keeping grants. MySQL has no
///         <c>CREATE OR REPLACE PROCEDURE</c>, so there it is dropped and created as two separate
///         statements — never with <c>DELIMITER</c>, which is a feature of the <c>mysql</c> command-line
///         client, not of the server. SQLite has no stored procedures and throws.
///     </para>
///     <para>
///         On PostgreSQL, <c>CREATE OR REPLACE</c> only replaces a procedure with the same argument
///         types. Changing them creates an overload beside the old one, so drop the old signature first
///         when changing parameters.
///     </para>
/// </remarks>
public sealed class StoredProcedureDefinition : IDatabaseObject
{
    internal const string Feature = "Stored procedures";

    /// <summary>Initializes a new instance of the <see cref="StoredProcedureDefinition" /> class.</summary>
    /// <param name="name">The procedure name.</param>
    /// <param name="parameters">The parameters, in order. <see langword="null" /> for none.</param>
    /// <param name="body">The routine body in the target engine's dialect.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="language">The PostgreSQL routine language. Ignored by every other engine.</param>
    public StoredProcedureDefinition(
        string name,
        IEnumerable<SqlParameterDefinition>? parameters,
        string body,
        string? schema = null,
        string language = "plpgsql")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Name = name;
        Parameters = RoutineSyntax.CopyParameters(parameters, allowOutput: true, nameof(parameters));
        Body = body.Trim();
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        Language = SqlSyntax.RequireSimpleIdentifier(language, nameof(language));
    }

    /// <summary>The procedure name.</summary>
    public string Name { get; }

    /// <summary>The parameters, in order.</summary>
    public IReadOnlyList<SqlParameterDefinition> Parameters { get; }

    /// <summary>The routine body.</summary>
    public string Body { get; }

    /// <summary>The schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>The PostgreSQL routine language.</summary>
    public string Language { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        var target = SqlSyntax.Qualify(dialect, Name, Schema);

        return dialect switch
        {
            SqlDialect.PostgreSql =>
            [
                $"CREATE OR REPLACE PROCEDURE {target}({RoutineSyntax.ParameterList(Parameters, withDirection: true, SqlDialect.PostgreSql)})\n"
                + $"LANGUAGE {Language}\n"
                + $"AS {SqlSyntax.DollarQuote(Body)}",
            ],
            SqlDialect.SqlServer =>
            [
                $"CREATE OR ALTER PROCEDURE {target}{RoutineSyntax.SqlServerProcedureParameters(Parameters)}\n"
                + $"AS\n{Body}",
            ],
            SqlDialect.MySql =>
            [
                .. DropSql(dialect, Name, Schema),
                $"CREATE PROCEDURE {target}({RoutineSyntax.ParameterList(Parameters, withDirection: true)})\n{Body}",
            ],
            SqlDialect.Sqlite => throw NotOnSqlite(),
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     By signature on PostgreSQL, which overloads by argument types: by name alone, dropping one
    ///     overload that was never created would drop a sibling that was.
    /// </remarks>
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect)
        => dialect == SqlDialect.PostgreSql
            ? [$"DROP PROCEDURE IF EXISTS {SqlSyntax.Qualify(dialect, Name, Schema)}({RoutineSyntax.PostgreSqlSignature(Parameters, withDirection: true)})"]
            : DropSql(dialect, Name, Schema);

    /// <remarks>
    ///     Dropped by name alone, without an argument list: that is the only form all three engines share,
    ///     and on PostgreSQL it is unambiguous as long as the routine is not overloaded. A definition drops
    ///     by its own signature there instead.
    /// </remarks>
    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string name, string? schema)
    {
        if (dialect == SqlDialect.Sqlite)
        {
            throw NotOnSqlite();
        }

        return [$"DROP PROCEDURE IF EXISTS {SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema)))}"];
    }

    internal static SqlFeatureNotSupportedException NotOnSqlite()
        => SqlFeatureNotSupportedException.For(
            Feature,
            SqlDialect.Sqlite,
            "SQLite has no server-side routines; run the statements from application code instead.");
}
