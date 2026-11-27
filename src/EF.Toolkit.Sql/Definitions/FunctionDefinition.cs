using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Definitions;

/// <summary>A user-defined function — scalar or table-valued — created or replaced in place.</summary>
/// <remarks>
///     <para>
///         Create one with <see cref="Scalar" /> or <see cref="Table" />. As with procedures, the body
///         is the engine's own, verbatim:
///     </para>
///     <list type="bullet">
///         <item>
///             PostgreSQL: what goes between the dollar quotes. Scalar functions default to
///             <c>plpgsql</c> (<c>BEGIN RETURN ...; END;</c>), table functions to <c>sql</c> (a plain
///             <c>SELECT</c>).
///         </item>
///         <item>
///             SQL Server: for a scalar function, what follows <c>AS</c> (<c>BEGIN RETURN ...; END</c>);
///             for a table function, the query of an inline table-valued function, which is the kind
///             the optimiser can expand into the calling query.
///         </item>
///         <item>MySQL: scalar functions only; the statement after <c>RETURNS</c>.</item>
///     </list>
///     <para>
///         SQLite functions are registered in-process on the connection, not in SQL, and MySQL has no
///         table-valued functions; both throw <see cref="SqlFeatureNotSupportedException" />.
///     </para>
/// </remarks>
public sealed class FunctionDefinition : IDatabaseObject
{
    internal const string ScalarFeature = "Scalar functions";
    internal const string TableFeature = "Table-valued functions";
    internal const string AnyFeature = "User-defined functions";

    private FunctionDefinition(
        FunctionKind kind,
        string name,
        IEnumerable<SqlParameterDefinition>? parameters,
        string? returnType,
        IEnumerable<SqlColumnDefinition>? columns,
        string body,
        string? schema,
        string language,
        string? characteristics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        Kind = kind;
        Name = name;
        Parameters = RoutineSyntax.CopyParameters(parameters, allowOutput: false, nameof(parameters));
        ReturnType = returnType?.Trim();
        Columns = columns?.ToArray() ?? [];
        Body = body.Trim();
        Schema = SqlSyntax.OptionalName(schema, nameof(schema));
        Language = SqlSyntax.RequireSimpleIdentifier(language, nameof(language));
        Characteristics = string.IsNullOrWhiteSpace(characteristics) ? null : characteristics.Trim();
    }

    /// <summary>Whether the function returns a value or a result set.</summary>
    public FunctionKind Kind { get; }

    /// <summary>The function name.</summary>
    public string Name { get; }

    /// <summary>The parameters, in order. All are input parameters.</summary>
    public IReadOnlyList<SqlParameterDefinition> Parameters { get; }

    /// <summary>The return type of a scalar function; <see langword="null" /> for a table function.</summary>
    public string? ReturnType { get; }

    /// <summary>
    ///     The result columns of a table function, which PostgreSQL declares as
    ///     <c>RETURNS TABLE (...)</c>. SQL Server infers them from the inline query and ignores these.
    /// </summary>
    public IReadOnlyList<SqlColumnDefinition> Columns { get; }

    /// <summary>The routine body.</summary>
    public string Body { get; }

    /// <summary>The schema, or <see langword="null" /> for the default schema.</summary>
    public string? Schema { get; }

    /// <summary>The PostgreSQL routine language. Ignored by every other engine.</summary>
    public string Language { get; }

    /// <summary>
    ///     Engine-specific routine options, inserted verbatim where the engine takes them:
    ///     <c>STABLE</c> or <c>IMMUTABLE PARALLEL SAFE</c> after <c>LANGUAGE</c> on PostgreSQL,
    ///     <c>DETERMINISTIC</c> or <c>READS SQL DATA</c> after <c>RETURNS</c> on MySQL (which requires
    ///     one of them when binary logging is on), and <c>WITH SCHEMABINDING</c>-style options as
    ///     <c>WITH ...</c> before <c>AS</c> on SQL Server.
    /// </summary>
    public string? Characteristics { get; }

    /// <summary>A function that returns a single value.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="parameters">The input parameters, in order. <see langword="null" /> for none.</param>
    /// <param name="returnType">The return type, as the engine spells it.</param>
    /// <param name="body">The routine body in the target engine's dialect.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="language">The PostgreSQL routine language.</param>
    /// <param name="characteristics">Engine-specific routine options; see <see cref="Characteristics" />.</param>
    /// <returns>The definition.</returns>
    public static FunctionDefinition Scalar(
        string name,
        IEnumerable<SqlParameterDefinition>? parameters,
        string returnType,
        string body,
        string? schema = null,
        string language = "plpgsql",
        string? characteristics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(returnType);
        return new(FunctionKind.Scalar, name, parameters, returnType, null, body, schema, language, characteristics);
    }

    /// <summary>A function that returns a result set.</summary>
    /// <param name="name">The function name.</param>
    /// <param name="parameters">The input parameters, in order. <see langword="null" /> for none.</param>
    /// <param name="columns">The result columns. Required by PostgreSQL; ignored by SQL Server.</param>
    /// <param name="body">The routine body in the target engine's dialect.</param>
    /// <param name="schema">The schema, or <see langword="null" /> for the default schema.</param>
    /// <param name="language">The PostgreSQL routine language.</param>
    /// <param name="characteristics">Engine-specific routine options; see <see cref="Characteristics" />.</param>
    /// <returns>The definition.</returns>
    public static FunctionDefinition Table(
        string name,
        IEnumerable<SqlParameterDefinition>? parameters,
        IEnumerable<SqlColumnDefinition> columns,
        string body,
        string? schema = null,
        string language = "sql",
        string? characteristics = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var copy = columns.ToArray();
        if (copy.Length == 0 || copy.Any(c => c is null))
        {
            throw new ArgumentException("A table function needs at least one column, and none can be null.", nameof(columns));
        }

        return new(FunctionKind.Table, name, parameters, null, copy, body, schema, language, characteristics);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetCreateSql(SqlDialect dialect)
    {
        var target = SqlSyntax.Qualify(dialect, Name, Schema);

        return dialect switch
        {
            SqlDialect.PostgreSql => [PostgreSqlCreate(target)],
            SqlDialect.SqlServer => [SqlServerCreate(target)],
            SqlDialect.MySql => [.. DropSql(dialect, Name, Schema), MySqlCreate(target)],
            SqlDialect.Sqlite => throw NotOnSqlite(Kind == FunctionKind.Scalar ? ScalarFeature : TableFeature),
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDropSql(SqlDialect dialect)
    {
        if (dialect == SqlDialect.Sqlite)
        {
            throw NotOnSqlite(Kind == FunctionKind.Scalar ? ScalarFeature : TableFeature);
        }

        if (dialect == SqlDialect.MySql && Kind == FunctionKind.Table)
        {
            throw NotOnMySql();
        }

        // By signature on PostgreSQL, which overloads by argument types: by name alone, dropping a
        // lookup(integer) that was never created would drop the lookup(text) that was, the only match.
        return dialect == SqlDialect.PostgreSql
            ? [$"DROP FUNCTION IF EXISTS {SqlSyntax.Qualify(dialect, Name, Schema)}({RoutineSyntax.PostgreSqlSignature(Parameters, withDirection: false)})"]
            : DropSql(dialect, Name, Schema);
    }

    /// <remarks>Dropped by name alone; see <see cref="StoredProcedureDefinition" />.</remarks>
    internal static IReadOnlyList<string> DropSql(SqlDialect dialect, string name, string? schema)
    {
        if (dialect == SqlDialect.Sqlite)
        {
            throw NotOnSqlite(AnyFeature);
        }

        return [$"DROP FUNCTION IF EXISTS {SqlSyntax.Qualify(dialect, name, SqlSyntax.OptionalName(schema, nameof(schema)))}"];
    }

    private string PostgreSqlCreate(string target)
    {
        var returns = Kind == FunctionKind.Scalar
            ? ReturnType
            : "TABLE (" + string.Join(", ", Columns.Select(c => $"{SqlSyntax.Quote(SqlDialect.PostgreSql, c.Name)} {c.StoreType}")) + ")";

        return $"CREATE OR REPLACE FUNCTION {target}({RoutineSyntax.ParameterList(Parameters, withDirection: false, SqlDialect.PostgreSql)})\n"
            + $"RETURNS {returns}\n"
            + $"LANGUAGE {Language}\n"
            + (Characteristics is null ? "" : Characteristics + "\n")
            + $"AS {SqlSyntax.DollarQuote(Body)}";
    }

    private string SqlServerCreate(string target)
    {
        var head = $"CREATE OR ALTER FUNCTION {target}({RoutineSyntax.SqlServerFunctionParameters(Parameters)})\n";
        var with = Characteristics is null ? "" : $"WITH {Characteristics}\n";

        return Kind == FunctionKind.Scalar
            ? head + $"RETURNS {ReturnType}\n" + with + $"AS\n{Body}"
            : head + "RETURNS TABLE\n" + with + $"AS\nRETURN (\n{SqlSyntax.TrimQuery(Body, SqlDialect.SqlServer)}\n)";
    }

    private string MySqlCreate(string target)
    {
        if (Kind == FunctionKind.Table)
        {
            throw NotOnMySql();
        }

        return $"CREATE FUNCTION {target}({RoutineSyntax.ParameterList(Parameters, withDirection: false)})\n"
            + $"RETURNS {ReturnType}\n"
            + (Characteristics is null ? "" : Characteristics + "\n")
            + Body;
    }

    internal static SqlFeatureNotSupportedException NotOnSqlite(string feature)
        => SqlFeatureNotSupportedException.For(
            feature,
            SqlDialect.Sqlite,
            "SQLite functions are registered on the connection from application code "
            + "(SqliteConnection.CreateFunction), not created in SQL.");

    internal static SqlFeatureNotSupportedException NotOnMySql()
        => SqlFeatureNotSupportedException.For(
            TableFeature,
            SqlDialect.MySql,
            "Use a view, or a stored procedure that returns a result set through QueryProcedureAsync.");
}
