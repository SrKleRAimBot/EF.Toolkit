using EFToolkit.Sql.Definitions;

namespace EFToolkit.Sql.Tests;

public sealed class MaterializedViewDefinitionTests
{
    [Fact]
    public void PostgreSql_drops_any_existing_view_and_creates_it()
        => new MaterializedViewDefinition("Totals", "SELECT 1 AS x", "rpt").GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
        [
            "DROP MATERIALIZED VIEW IF EXISTS \"rpt\".\"Totals\"",
            "CREATE MATERIALIZED VIEW \"rpt\".\"Totals\" AS\nSELECT 1 AS x",
        ]);

    [Fact]
    public void With_no_data_follows_the_query_even_when_it_ended_in_a_semicolon()
        => new MaterializedViewDefinition("Totals", "SELECT 1 AS x;", withNoData: true).GetCreateSql(SqlDialect.PostgreSql)[1]
            .ShouldBe("CREATE MATERIALIZED VIEW \"Totals\" AS\nSELECT 1 AS x\nWITH NO DATA");

    [Fact]
    public void Drop_is_if_exists()
        => new MaterializedViewDefinition("Totals", "SELECT 1").GetDropSql(SqlDialect.PostgreSql)
            .ShouldBe(["DROP MATERIALIZED VIEW IF EXISTS \"Totals\""]);

    [Theory]
    [InlineData(false, "REFRESH MATERIALIZED VIEW \"rpt\".\"Totals\"")]
    [InlineData(true, "REFRESH MATERIALIZED VIEW CONCURRENTLY \"rpt\".\"Totals\"")]
    public void Refresh_optionally_runs_concurrently(bool concurrently, string expected)
        => MaterializedViewDefinition.RefreshSql(SqlDialect.PostgreSql, "Totals", "rpt", concurrently).ShouldBe(expected);

    [Theory]
    [InlineData(SqlDialect.SqlServer)]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Sqlite)]
    public void Every_other_engine_refuses_naming_the_feature(SqlDialect dialect)
    {
        var view = new MaterializedViewDefinition("Totals", "SELECT 1");

        var create = Should.Throw<SqlFeatureNotSupportedException>(() => view.GetCreateSql(dialect));
        create.Feature.ShouldBe("Materialized views");
        create.Dialect.ShouldBe(dialect);

        Should.Throw<SqlFeatureNotSupportedException>(() => view.GetDropSql(dialect));
        Should.Throw<SqlFeatureNotSupportedException>(() => MaterializedViewDefinition.RefreshSql(dialect, "Totals", null, false));
    }

    [Fact]
    public void SqlServer_points_at_indexed_views()
        => Should.Throw<SqlFeatureNotSupportedException>(
                () => new MaterializedViewDefinition("Totals", "SELECT 1").GetCreateSql(SqlDialect.SqlServer))
            .Message.ShouldContain("indexed view");

    [Fact]
    public void The_definition_keeps_what_it_was_given()
    {
        var view = new MaterializedViewDefinition("Totals", "SELECT 1", "rpt", withNoData: true);

        view.Name.ShouldBe("Totals");
        view.Sql.ShouldBe("SELECT 1");
        view.Schema.ShouldBe("rpt");
        view.WithNoData.ShouldBeTrue();
    }

    [Fact]
    public void Blank_arguments_are_refused()
    {
        Should.Throw<ArgumentException>(() => new MaterializedViewDefinition(" ", "SELECT 1"));
        Should.Throw<ArgumentException>(() => new MaterializedViewDefinition("m", ""));
        Should.Throw<ArgumentException>(() => MaterializedViewDefinition.RefreshSql(SqlDialect.PostgreSql, "", null, false));
    }
}
