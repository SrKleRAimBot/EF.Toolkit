using EFToolkit.Sql.Dialects;

namespace EFToolkit.Sql.Tests;

public sealed class SqlSyntaxTests
{
    [Theory]
    [InlineData(SqlDialect.PostgreSql, "Orders", "\"Orders\"")]
    [InlineData(SqlDialect.PostgreSql, "we\"ird", "\"we\"\"ird\"")]
    [InlineData(SqlDialect.Sqlite, "a\"\"b", "\"a\"\"\"\"b\"")]
    [InlineData(SqlDialect.SqlServer, "Orders", "[Orders]")]
    [InlineData(SqlDialect.SqlServer, "a]b[c", "[a]]b[c]")]
    [InlineData(SqlDialect.MySql, "Orders", "`Orders`")]
    [InlineData(SqlDialect.MySql, "a`b", "`a``b`")]
    public void Identifiers_are_quoted_with_embedded_quotes_doubled(SqlDialect dialect, string name, string expected)
        => SqlSyntax.Quote(dialect, name).ShouldBe(expected);

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "\"sales\".\"Orders\"")]
    [InlineData(SqlDialect.SqlServer, "[sales].[Orders]")]
    [InlineData(SqlDialect.MySql, "`sales`.`Orders`")]
    [InlineData(SqlDialect.Sqlite, "\"sales\".\"Orders\"")]
    public void Schema_qualified_names_quote_both_parts(SqlDialect dialect, string expected)
        => SqlSyntax.Qualify(dialect, "Orders", "sales").ShouldBe(expected);

    [Fact]
    public void An_absent_schema_leaves_a_one_part_name()
        => SqlSyntax.Qualify(SqlDialect.SqlServer, "Orders", null).ShouldBe("[Orders]");

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\0b")]
    public void Unusable_identifiers_are_refused(string name)
        => Should.Throw<ArgumentException>(() => SqlSyntax.Quote(SqlDialect.PostgreSql, name));

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "it's", "'it''s'")]
    [InlineData(SqlDialect.Sqlite, "it's", "'it''s'")]
    [InlineData(SqlDialect.SqlServer, "it's", "N'it''s'")]
    [InlineData(SqlDialect.MySql, "it's \\ here", "'it''s \\\\ here'")]
    [InlineData(SqlDialect.PostgreSql, "back\\slash", "'back\\slash'")]
    public void String_literals_are_escaped_per_engine(SqlDialect dialect, string value, string expected)
        => SqlSyntax.Literal(dialect, value).ShouldBe(expected);

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("_x1")]
    [InlineData("A")]
    public void Plain_identifiers_are_accepted(string name)
        => SqlSyntax.RequireSimpleIdentifier(name, "p").ShouldBe(name);

    [Theory]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("a;drop")]
    [InlineData("a\"b")]
    [InlineData("")]
    public void Anything_but_a_plain_identifier_is_refused(string name)
        => Should.Throw<ArgumentException>(() => SqlSyntax.RequireSimpleIdentifier(name, "p"));

    [Fact]
    public void A_body_without_dollar_quotes_uses_plain_dollar_quoting()
        => SqlSyntax.DollarQuote("BEGIN END;").ShouldBe("$$\nBEGIN END;\n$$");

    [Fact]
    public void A_body_containing_double_dollars_gets_a_tag_it_does_not_contain()
        => SqlSyntax.DollarQuote("SELECT $$x$$;").ShouldBe("$body$\nSELECT $$x$$;\n$body$");

    [Fact]
    public void A_body_containing_the_fallback_tag_too_gets_a_numbered_one()
        => SqlSyntax.DollarQuote("$$ $body$ $body1$").ShouldBe("$body2$\n$$ $body$ $body1$\n$body2$");

    [Theory]
    [InlineData("SELECT 1", "SELECT 1")]
    [InlineData("  SELECT 1;  ", "SELECT 1")]
    [InlineData("SELECT 1; ;\n", "SELECT 1")]
    [InlineData("SELECT 1 AS id; -- initial query", "SELECT 1 AS id -- initial query")]
    [InlineData("SELECT 1 -- note", "SELECT 1 -- note")]
    [InlineData("SELECT 1; /* done */ ;\n-- end", "SELECT 1 /* done */ \n-- end")]
    [InlineData("SELECT ';-- not a comment'", "SELECT ';-- not a comment'")]
    [InlineData("SELECT 'it''s'; -- note", "SELECT 'it''s' -- note")]
    [InlineData("SELECT \"a;--b\" FROM t;", "SELECT \"a;--b\" FROM t")]
    [InlineData("SELECT $body$ ; -- $body$", "SELECT $body$ ; -- $body$")]
    [InlineData("SELECT $1; -- p", "SELECT $1 -- p")]
    [InlineData("SELECT data #> '{a}' FROM t;", "SELECT data #> '{a}' FROM t")]
    [InlineData("SELECT 'it\\'s';", "SELECT 'it\\'s'")]
    [InlineData("SELECT 3--2;", "SELECT 3--2")]
    [InlineData("SELECT 1 AS id /*! WHERE FALSE */;", "SELECT 1 AS id /*! WHERE FALSE */")]
    [InlineData("SELECT 1 /*+ hint */ ; -- y", "SELECT 1 /*+ hint */  -- y")]
    [InlineData("SELECT 1 AS id -- note\rWHERE FALSE;", "SELECT 1 AS id -- note\rWHERE FALSE")]
    [InlineData("SELECT E'a\\';-- tail' AS value", "SELECT E'a\\';-- tail' AS value")]
    [InlineData("SELECT 'a\\';-- tail' AS value;", "SELECT 'a\\';-- tail' AS value")]
    [InlineData("SELECT 1 AS value\u00A0", "SELECT 1 AS value\u00A0")]
    [InlineData("SELECT 1 AS value\u00A0;", "SELECT 1 AS value\u00A0")]
    [InlineData("SELECT $\u00E9$x;-- tail$\u00E9$ AS value", "SELECT $\u00E9$x;-- tail$\u00E9$ AS value")]
    [InlineData("SELECT $1, $2;", "SELECT $1, $2")]
    public void Queries_lose_surrounding_whitespace_and_trailing_semicolons(string sql, string expected)
        => SqlSyntax.TrimQuery(sql).ShouldBe(expected);

    [Fact]
    public void Braces_are_escaped_for_composite_formatting()
    {
        var escaped = SqlSyntax.EscapeFormat("SELECT '{\"a\":1}'");

        escaped.ShouldBe("SELECT '{{\"a\":1}}'");
        string.Format(System.Globalization.CultureInfo.InvariantCulture, escaped).ShouldBe("SELECT '{\"a\":1}'");
    }

    [Fact]
    public void Blank_schemas_are_refused_but_null_is_the_default()
    {
        SqlSyntax.OptionalName(null, "schema").ShouldBeNull();
        SqlSyntax.OptionalName("dbo", "schema").ShouldBe("dbo");
        Should.Throw<ArgumentException>(() => SqlSyntax.OptionalName(" ", "schema"));
    }

    [Fact]
    public void Undefined_dialect_values_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => SqlSyntax.Quote((SqlDialect)42, "x"));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlSyntax.Literal((SqlDialect)42, "x"));
        Should.Throw<ArgumentOutOfRangeException>(() => SqlSyntax.EngineName((SqlDialect)42));
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "PostgreSQL")]
    [InlineData(SqlDialect.SqlServer, "SQL Server")]
    [InlineData(SqlDialect.MySql, "MySQL")]
    [InlineData(SqlDialect.Sqlite, "SQLite")]
    public void Engines_have_display_names(SqlDialect dialect, string expected)
        => SqlSyntax.EngineName(dialect).ShouldBe(expected);
}
