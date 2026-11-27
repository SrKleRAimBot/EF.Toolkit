using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Sessions;

/// <summary>Renders the statement that sets a session-context value, per dialect.</summary>
/// <remarks>
///     The value is always a bound parameter, and on PostgreSQL and SQL Server so is the key. MySQL is
///     the exception: a user variable's name is part of the statement's syntax and cannot be bound, so
///     the key is validated against MySQL's own rule for variable names and written bare.
/// </remarks>
internal static partial class SessionContextSql
{
    internal const string Feature = "Session context";

    /// <summary>Refuses a key the engine would reject or misinterpret.</summary>
    public static void ValidateKey(SqlDialect dialect, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        switch (dialect)
        {
            case SqlDialect.PostgreSql when !PostgreSqlKey().IsMatch(key):
                // set_config refuses an undotted name outright — those are reserved for the server's own
                // settings — and current_setting(name, true) quietly reads NULL for any name it does not
                // know, so a typo would otherwise fail closed with no error at all.
                throw new ArgumentException(
                    $"'{key}' is not a valid PostgreSQL custom setting name. Use a dotted name such as "
                    + "'app.tenant_id': a prefix and a name, each letters, digits and underscores.",
                    nameof(key));
            case SqlDialect.SqlServer when key.Length > 128:
                throw new ArgumentException(
                    $"SQL Server session-context keys are at most 128 characters; '{key}' is {key.Length}.",
                    nameof(key));
            case SqlDialect.MySql when !MySqlVariable().IsMatch(key):
                throw new ArgumentException(
                    $"'{key}' is not a valid MySQL user variable name. Use letters, digits, '.', '_' and '$'.",
                    nameof(key));
            case SqlDialect.Sqlite:
                throw NotOnSqlite();
            case SqlDialect.PostgreSql or SqlDialect.SqlServer or SqlDialect.MySql:
                return;
            default:
                throw SqlSyntax.UndefinedDialect(dialect);
        }
    }

    /// <summary>The statement that sets one value, with the given placeholders for key and value.</summary>
    public static string Statement(SqlDialect dialect, string key, string keyPlaceholder, string valuePlaceholder)
    {
        ValidateKey(dialect, key);

        return dialect switch
        {
            // is_local false: the value lasts for the session, not just the current transaction, so
            // it holds across the separate transactions SaveChanges and queries run in.
            SqlDialect.PostgreSql => $"SELECT set_config({keyPlaceholder}, {valuePlaceholder}, false)",
            SqlDialect.SqlServer => $"EXEC sp_set_session_context @key = {keyPlaceholder}, @value = {valuePlaceholder}",
            SqlDialect.MySql => $"SET @{key} = {valuePlaceholder}",
            _ => throw SqlSyntax.UndefinedDialect(dialect),
        };
    }

    /// <summary>Whether the statement binds the key as well as the value.</summary>
    public static bool BindsKey(SqlDialect dialect) => dialect != SqlDialect.MySql;

    /// <summary>A statement and arguments for EF's raw SQL (<c>{0}</c>-style placeholders).</summary>
    public static (string Sql, object?[] Arguments) ForRawSql(SqlDialect dialect, string key, object? value)
    {
        var converted = ConvertValue(dialect, value);

        return BindsKey(dialect)
            ? (Statement(dialect, key, "{0}", "{1}"), [key, converted])
            : (Statement(dialect, key, "", "{0}"), [converted]);
    }

    /// <summary>A command setting every value in one round trip.</summary>
    public static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) ForCommand(
        SqlDialect dialect,
        IReadOnlyList<(string Key, object? Value)> values)
    {
        var statements = new List<string>(values.Count);
        var parameters = new List<(string, object)>();
        var prefix = ParameterPrefix(values.Select(v => v.Key));

        for (var i = 0; i < values.Count; i++)
        {
            var (key, value) = values[i];
            var keyName = string.Create(CultureInfo.InvariantCulture, $"@{prefix}key{i}");
            var valueName = string.Create(CultureInfo.InvariantCulture, $"@{prefix}value{i}");

            statements.Add(Statement(dialect, key, keyName, valueName));
            if (BindsKey(dialect))
            {
                parameters.Add((keyName, key));
            }

            // A DbParameter needs DBNull for null; EF's raw SQL, by contrast, wants null itself.
            parameters.Add((valueName, ConvertValue(dialect, value) ?? DBNull.Value));
        }

        return (string.Join(";\n", statements), parameters);
    }

    /// <summary>
    ///     A prefix no key begins with, so no parameter named from it can be a key. MySQL writes a key as
    ///     the user variable @key beside the parameters, and a connector binds every @name it sees: a key
    ///     named like a parameter would have the value bound in its place too — SET 7 = 7.
    /// </summary>
    public static string ParameterPrefix(IEnumerable<string> keys)
    {
        var all = keys.ToList();
        var prefix = "ef_toolkit_";
        while (all.Exists(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            prefix = "x" + prefix;
        }

        return prefix;
    }

    /// <summary>Creates the command from <see cref="ForCommand" /> on an open connection.</summary>
    public static DbCommand CreateCommand(
        DbConnection connection,
        SqlDialect dialect,
        IReadOnlyList<(string Key, object? Value)> values)
    {
        var (sql, parameters) = ForCommand(dialect, values);

        var command = connection.CreateCommand();
        try
        {
#pragma warning disable CA2100 // Every value is a parameter; the text holds only validated keys.
            command.CommandText = sql;
#pragma warning restore CA2100
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;

                // SqlClient infers SQL Server's datetime for a DateTime, which rounds to 1/300 s: 12:30:00.001
                // would be stored as 12:30:00.000, another datetime2 tenant's key. datetime2 keeps it whole.
                if (dialect == SqlDialect.SqlServer && value is DateTime)
                {
                    parameter.DbType = System.Data.DbType.DateTime2;
                }

                command.Parameters.Add(parameter);
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The value as the engine should receive it.</summary>
    /// <remarks>
    ///     PostgreSQL settings are text, so the value is formatted invariantly — a culture that writes
    ///     <c>1,5</c> or day-first dates would otherwise store something the policy's cast rejects —
    ///     and a null clears the setting to the empty string, which the generated policies read as no
    ///     tenant. SQL Server stores a <c>sql_variant</c>, so the value keeps its type there; a null
    ///     clears the key. MySQL user variables are likewise typed.
    /// </remarks>
    public static object? ConvertValue(SqlDialect dialect, object? value)
    {
        if (dialect != SqlDialect.PostgreSql)
        {
            return value is DBNull ? null : value;
        }

        return value switch
        {
            null or DBNull => "",
            string s => s,
            bool b => b ? "true" : "false",
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),

            // Their invariant defaults are 03/04/2026 — April 3 under DateStyle DMY — and 12:30, without
            // the seconds: either would name another tenant's key. ISO 8601 reads the same under every
            // DateStyle.
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    internal static SqlFeatureNotSupportedException NotOnSqlite()
        => SqlFeatureNotSupportedException.For(
            Feature,
            SqlDialect.Sqlite,
            "SQLite is an in-process database with no session state on the server side.");

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgreSqlKey();

    [GeneratedRegex(@"^[A-Za-z0-9._$]+$", RegexOptions.CultureInvariant)]
    private static partial Regex MySqlVariable();
}
