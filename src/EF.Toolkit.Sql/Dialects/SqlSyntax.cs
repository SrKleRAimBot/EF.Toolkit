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
    public static string TrimQuery(string sql, SqlDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // A semicolon followed only by a comment ends the statement just as one at the very end does:
        // "SELECT 1; -- note" embedded before WITH NO DATA would make that a second statement. So every
        // semicolon after the query's last significant character is removed — and only those: comments
        // are left exactly where they are (what follows the query is always put on a line of its own),
        // so nothing the engine would run can be cut away. A scan that cannot tell where the query ends
        // leaves a semicolon in place at worst, which the engine refuses, never a query that means less.
        if (TrailingSemicolons(sql, dialect) is { } semicolons)
        {
            var builder = new System.Text.StringBuilder(sql);
            for (var k = semicolons.Count - 1; k >= 0; k--)
            {
                builder.Remove(semicolons[k], 1);
            }

            return builder.ToString().Trim(SqlWhitespace);
        }

        // A string or comment the scan could not see the end of — a backslash escape, say: the trailing
        // semicolons alone are trimmed, as they always were.
        var trimmed = sql.Trim(SqlWhitespace);
        while (trimmed.EndsWith(';'))
        {
            trimmed = trimmed[..^1].TrimEnd(SqlWhitespace);
        }

        return trimmed;
    }

    // What every engine reads as whitespace between tokens. Not char.IsWhiteSpace: PostgreSQL takes a
    // no-break space into an unquoted identifier, so trimming one would rename a column.
    private static readonly char[] SqlWhitespace = [' ', '\t', '\n', '\r', '\f', '\v'];

    private static bool IsSqlWhitespace(char c) => Array.IndexOf(SqlWhitespace, c) >= 0;

    // The positions of the semicolons after the last significant character — anything but whitespace,
    // a comment or a semicolon; null if the text ends inside a quoted string, a quoted identifier, a
    // block comment or a dollar quote. Comments are read as the engine reads them — a comment taken
    // for code could hide a quote, and code taken for a comment could hide one too:
    //   -- starts a comment, ending at a line feed or a carriage return; on MySQL only when an ASCII
    //        space or control character or the end follows it (3--2 is 3 - -2 there, and so is
    //        --\u0085x: MySQL reads its bytes, not .NET's wider idea of a control character);
    //   #  starts one on MySQL alone — on PostgreSQL it is an operator (#>);
    //   /* */ is a comment, but /*! … */ and /*+ … */ are MySQL's executable comments and hints.
    private static List<int>? TrailingSemicolons(string sql, SqlDialect dialect)
    {
        var semicolons = new List<int>();
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (IsSqlWhitespace(c))
            {
                i++;
            }
            else if (c == ';')
            {
                semicolons.Add(i);
                i++;
            }
            else if ((c == '-' && next == '-'
                    && (dialect != SqlDialect.MySql || i + 2 == sql.Length || sql[i + 2] is <= ' ' or '\x7F'))
                || (c == '#' && dialect == SqlDialect.MySql))
            {
                // PostgreSQL and SQL Server end it at a carriage return as well; MySQL and SQLite only
                // at a line feed, so a carriage return there is still inside the comment.
                var lineEnd = dialect is SqlDialect.PostgreSql or SqlDialect.SqlServer
                    ? sql.IndexOfAny(['\n', '\r'], i)
                    : sql.IndexOf('\n', i);
                i = lineEnd < 0 ? sql.Length : lineEnd + 1;
            }
            else if (c == '/' && next == '*')
            {
                var close = BlockCommentEnd(sql, i, nested: dialect is SqlDialect.PostgreSql or SqlDialect.SqlServer);
                if (close < 0)
                {
                    return null;
                }

                var executable = i + 2 < sql.Length && sql[i + 2] is '!' or '+';
                if (executable)
                {
                    semicolons.Clear();
                }

                i = close + 2;
            }
            // [ ] quotes an identifier on SQL Server and SQLite only — on PostgreSQL it subscripts and
            // builds arrays, ARRAY[']; x'] — and backticks on MySQL and SQLite.
            else if (c is '\'' or '"'
                || (c == '[' && dialect is SqlDialect.SqlServer or SqlDialect.Sqlite)
                || (c == '`' && dialect is SqlDialect.MySql or SqlDialect.Sqlite))
            {
                // Doubling the closing character escapes it inside — 'it''s', [a]]b] — so a doubled one
                // is stepped over rather than taken for the end. (For [ ] there is no other way to tell:
                // ]] does not reopen a run the way '' does.)
                var closing = c == '[' ? ']' : c;
                var close = sql.IndexOf(closing, i + 1);
                while (close >= 0 && close + 1 < sql.Length && sql[close + 1] == closing)
                {
                    close = sql.IndexOf(closing, close + 2);
                }

                // A backslash may escape the quote — in PostgreSQL's E'…' and in MySQL's strings — so
                // where the run ends is not certain: the scan gives up rather than guess.
                if (close < 0 || sql.AsSpan(i + 1, close - i - 1).Contains('\\'))
                {
                    return null;
                }

                semicolons.Clear();
                i = close + 1;
            }
            else if (char.IsLetter(c) || c == '_' || c > '\x7F')
            {
                // A word — an unquoted identifier or keyword — is one token, $ and all: a$t$ is an
                // identifier on PostgreSQL, and its $t$ must not be taken to open a dollar quote.
                semicolons.Clear();
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' || sql[i] > '\x7F'))
                {
                    i++;
                }
            }
            // Dollar quotes are PostgreSQL's alone: on MySQL 1$a$ is an identifier, and $a$ elsewhere
            // just text — pairing them would hide quotes that are really there.
            else if (c == '$' && dialect == SqlDialect.PostgreSql && DollarTag().Match(sql, i) is { Success: true } tag)
            {
                var close = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    return null;
                }

                semicolons.Clear();
                i = close + tag.Length;
            }
            else
            {
                semicolons.Clear();
                i++;
            }
        }

        return semicolons;
    }

    // Where the block comment opened at start ends: the index of its closing */, or -1. PostgreSQL and
    // SQL Server nest them — /* a /* b */ still a */ — MySQL and SQLite end at the first */.
    private static int BlockCommentEnd(string sql, int start, bool nested)
    {
        if (!nested)
        {
            return sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
        }

        var depth = 1;
        var j = start + 2;
        while (j + 1 < sql.Length)
        {
            if (sql[j] == '/' && sql[j + 1] == '*')
            {
                depth++;
                j += 2;
            }
            else if (sql[j] == '*' && sql[j + 1] == '/')
            {
                if (--depth == 0)
                {
                    return j;
                }

                j += 2;
            }
            else
            {
                j++;
            }
        }

        return -1;
    }

    // $$ or $tag$ opening a dollar-quoted body. Wider than PostgreSQL's own tag syntax — any run with
    // no $, SQL whitespace or quote, $é$ and a no-break space included — on purpose: taking too much for a quote can only keep
    // a semicolon (or end the scan unfinished, for the plain trim), while missing one could take a
    // semicolon out of the quoted text. $1 alone is a parameter: it has no closing $.
    [GeneratedRegex(@"\G\$[^$ \t\n\r\f\v'""`;]*\$", RegexOptions.CultureInvariant)]
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
