using System.Globalization;
using System.Text.RegularExpressions;

namespace EFToolkit.Sql.Dialects;

/// <summary>Quoting, escaping and naming rules per dialect.</summary>
/// <remarks>
///     Everything that ends up in generated SQL as text rather than as a bound parameter passes
///     through here. Names are always quoted — even ones that would be legal bare — because the
///     alternative is a list of reserved words per engine and version that is never complete, and a
///     name EF itself mapped (<c>"Order"</c>, <c>[User]</c>) is exactly the kind that collides.
/// </remarks>
internal static partial class SqlSyntax
{
    /// <summary>The engine's display name, for error messages.</summary>
    public static string EngineName(SqlDialect dialect)
        => dialect switch
        {
            SqlDialect.PostgreSql => "PostgreSQL",
            SqlDialect.SqlServer => "SQL Server",
            SqlDialect.MySql => "MySQL",
            SqlDialect.Sqlite => "SQLite",
            _ => throw UndefinedDialect(dialect),
        };

    /// <summary>Quotes one identifier, escaping the engine's own quote character inside it.</summary>
    public static string Quote(SqlDialect dialect, string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        // No engine accepts NUL in an identifier, and some drivers truncate at it — which would turn
        // the quoted name into a prefix of itself with the closing quote cut off.
        if (identifier.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("An identifier cannot contain a NUL character.", nameof(identifier));
        }

        return dialect switch
        {
            SqlDialect.PostgreSql or SqlDialect.Sqlite => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            SqlDialect.SqlServer => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
            SqlDialect.MySql => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`",
            _ => throw UndefinedDialect(dialect),
        };
    }

    /// <summary>A quoted, optionally schema-qualified name.</summary>
    /// <remarks>
    ///     On MySQL the "schema" is the database, and on SQLite it is the name of an attached
    ///     database; both use the same two-part syntax.
    /// </remarks>
    public static string Qualify(SqlDialect dialect, string name, string? schema)
        => schema is null ? Quote(dialect, name) : Quote(dialect, schema) + "." + Quote(dialect, name);

    /// <summary>A string literal, escaped for the engine's default settings.</summary>
    /// <remarks>
    ///     MySQL treats a backslash as an escape inside string literals unless
    ///     <c>NO_BACKSLASH_ESCAPES</c> is set, so backslashes are doubled there; under that SQL mode a
    ///     literal containing one would come out doubled, which is why values that matter at runtime
    ///     are bound as parameters rather than rendered here.
    /// </remarks>
    public static string Literal(SqlDialect dialect, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var doubled = value.Replace("'", "''", StringComparison.Ordinal);

        return dialect switch
        {
            SqlDialect.PostgreSql or SqlDialect.Sqlite => "'" + doubled + "'",
            SqlDialect.SqlServer => "N'" + doubled + "'",
            SqlDialect.MySql => "'" + doubled.Replace("\\", "\\\\", StringComparison.Ordinal) + "'",
            _ => throw UndefinedDialect(dialect),
        };
    }

    /// <summary>
    ///     Requires <paramref name="value" /> to be a plain identifier — letters, digits and
    ///     underscores, not starting with a digit.
    /// </summary>
    /// <remarks>
    ///     Used for names that cannot be quoted: routine parameter names, which the routine body
    ///     refers to bare, and a PostgreSQL routine's language. Quoting a parameter would make
    ///     <c>"TenantId"</c> in the signature a different name from <c>TenantId</c> in the body.
    /// </remarks>
    public static string RequireSimpleIdentifier(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);

        if (!SimpleIdentifier().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a plain identifier. Use letters, digits and underscores only, "
                + "not starting with a digit: the name appears unquoted in the generated SQL.",
                paramName);
        }

        return value;
    }

    /// <summary>Requires an optional schema to be either absent or a real name.</summary>
    public static string? OptionalName(string? value, string paramName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A schema must be null (the default schema) or a non-blank name.",
                paramName);
        }

        return value;
    }

    /// <summary>
    ///     Wraps a PostgreSQL routine body in dollar quotes whose tag does not occur in the body.
    /// </summary>
    /// <remarks>
    ///     A body that itself contains <c>$$</c> — a nested function definition, a string built with
    ///     dollar quoting — would end a <c>$$</c>-quoted body early, and the remainder would run as
    ///     top-level SQL.
    /// </remarks>
    public static string DollarQuote(string body)
    {
        var tag = "$$";
        for (var i = 0; body.Contains(tag, StringComparison.Ordinal); i++)
        {
            tag = i == 0 ? "$body$" : string.Create(CultureInfo.InvariantCulture, $"$body{i}$");
        }

        return tag + "\n" + body + "\n" + tag;
    }

    /// <summary>A query with surrounding whitespace and trailing semicolons removed.</summary>
    /// <remarks>
    ///     Queries are embedded in larger statements (<c>AS ... WITH NO DATA</c>, <c>RETURN (...)</c>),
    ///     where a trailing semicolon would end the statement early.
    /// </remarks>
    public static string TrimQuery(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // A semicolon followed only by a comment ends the statement just as one at the very end does:
        // "SELECT 1; -- note" embedded before WITH NO DATA would make that a second statement. So the
        // query is cut after its last significant character — not whitespace, a comment or a semicolon
        // — found by a scan that steps over quoted text, quoted identifiers and dollar-quoted bodies.
        if (LastSignificantEnd(sql) is { } end)
        {
            return sql[..end].Trim();
        }

        // A string or comment the scan could not see the end of — a MySQL backslash escape, say: the
        // trailing semicolons alone are trimmed, as they always were.
        var trimmed = sql.Trim();
        while (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed;
    }

    // The index just past the last character that is not whitespace, a comment or a semicolon; null if
    // the text ends inside a quoted string, a quoted identifier, a block comment or a dollar quote.
    // Only -- and /* */ are comments: # is one on MySQL alone, and an operator on PostgreSQL (#>).
    private static int? LastSignificantEnd(string sql)
    {
        var end = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c) || c == ';')
            {
                i++;
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                i = close + 2;
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                // Doubling the quote escapes it inside, which the scan sees as two quoted runs.
                var closing = c == '[' ? ']' : c;
                var close = sql.IndexOf(closing, i + 1);
                if (close < 0)
                {
                    return null;
                }

                i = end = close + 1;
            }
            else if (c == '$' && DollarTag().Match(sql, i) is { Success: true } tag)
            {
                var close = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                i = end = close + tag.Length;
            }
            else
            {
                i = end = i + 1;
            }
        }

        return end;
    }

    // $$ or $tag$ opening a dollar-quoted body; $1 is a parameter, not a quote.
    [GeneratedRegex(@"\G\$([A-Za-z_][A-Za-z0-9_]*)?\$", RegexOptions.CultureInvariant)]
    private static partial Regex DollarTag();

    /// <summary>
    ///     Escapes composite-format braces so text survives EF's raw-SQL formatting, which runs
    ///     <c>string.Format</c> over every raw statement whether or not it has arguments.
    /// </summary>
    public static string EscapeFormat(string sql)
        => sql.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    public static ArgumentOutOfRangeException UndefinedDialect(SqlDialect dialect)
        => new(nameof(dialect), dialect, "Not a defined SqlDialect value.");

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifier();
}
