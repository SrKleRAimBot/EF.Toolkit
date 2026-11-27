using EFToolkit.Sql.Definitions;

namespace EFToolkit.Sql.Tests;

public sealed class ViewDefinitionTests
{
    private static readonly ViewDefinition View = new("ActiveCustomers", "SELECT 1 AS x;", "sales");

    [Fact]
    public void PostgreSql_replaces_in_place()
        => View.GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
            ["CREATE OR REPLACE VIEW \"sales\".\"ActiveCustomers\" AS\nSELECT 1 AS x"]);

    [Fact]
    public void SqlServer_creates_or_alters()
        => View.GetCreateSql(SqlDialect.SqlServer).ShouldBe(
            ["CREATE OR ALTER VIEW [sales].[ActiveCustomers] AS\nSELECT 1 AS x"]);

    [Fact]
    public void MySql_replaces_in_place()
        => View.GetCreateSql(SqlDialect.MySql).ShouldBe(
            ["CREATE OR REPLACE VIEW `sales`.`ActiveCustomers` AS\nSELECT 1 AS x"]);

    [Fact]
    public void Sqlite_drops_and_creates_as_separate_statements()
        => View.GetCreateSql(SqlDialect.Sqlite).ShouldBe(
        [
            "DROP VIEW IF EXISTS \"sales\".\"ActiveCustomers\"",
            "CREATE VIEW \"sales\".\"ActiveCustomers\" AS\nSELECT 1 AS x",
        ]);

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "DROP VIEW IF EXISTS \"sales\".\"ActiveCustomers\"")]
    [InlineData(SqlDialect.SqlServer, "DROP VIEW IF EXISTS [sales].[ActiveCustomers]")]
    [InlineData(SqlDialect.MySql, "DROP VIEW IF EXISTS `sales`.`ActiveCustomers`")]
    [InlineData(SqlDialect.Sqlite, "DROP VIEW IF EXISTS \"sales\".\"ActiveCustomers\"")]
    public void Drop_is_if_exists_on_every_engine(SqlDialect dialect, string expected)
        => View.GetDropSql(dialect).ShouldBe([expected]);

    [Fact]
    public void Names_with_quote_characters_are_escaped()
    {
        var view = new ViewDefinition("odd\"]`name", "SELECT 1");

        view.GetCreateSql(SqlDialect.PostgreSql)[0].ShouldStartWith("CREATE OR REPLACE VIEW \"odd\"\"]`name\" AS");
        view.GetCreateSql(SqlDialect.SqlServer)[0].ShouldStartWith("CREATE OR ALTER VIEW [odd\"]]`name] AS");
        view.GetCreateSql(SqlDialect.MySql)[0].ShouldStartWith("CREATE OR REPLACE VIEW `odd\"]``name` AS");
    }

    [Fact]
    public void Without_a_schema_the_name_is_one_part()
        => new ViewDefinition("v", "SELECT 1").GetDropSql(SqlDialect.SqlServer).ShouldBe(["DROP VIEW IF EXISTS [v]"]);

    [Fact]
    public void The_definition_keeps_what_it_was_given()
    {
        View.Name.ShouldBe("ActiveCustomers");
        View.Sql.ShouldBe("SELECT 1 AS x;");
        View.Schema.ShouldBe("sales");
    }

    [Fact]
    public void Blank_arguments_are_refused()
    {
        Should.Throw<ArgumentException>(() => new ViewDefinition("", "SELECT 1"));
        Should.Throw<ArgumentException>(() => new ViewDefinition("v", " "));
        Should.Throw<ArgumentException>(() => new ViewDefinition("v", "SELECT 1", ""));
    }

    [Fact]
    public void Undefined_dialects_are_refused()
        => Should.Throw<ArgumentOutOfRangeException>(() => View.GetCreateSql((SqlDialect)42));
}
