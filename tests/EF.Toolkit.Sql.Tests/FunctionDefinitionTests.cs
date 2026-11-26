using EFToolkit.Sql.Definitions;

namespace EFToolkit.Sql.Tests;

public sealed class FunctionDefinitionTests
{
    private static readonly SqlParameterDefinition[] Parameters = [new("a", "integer"), new("b", "integer")];

    private static readonly FunctionDefinition Scalar =
        FunctionDefinition.Scalar("add", Parameters, "integer", "BEGIN RETURN a + b; END;", "math");

    private static readonly FunctionDefinition Table = FunctionDefinition.Table(
        "orders_for",
        [new SqlParameterDefinition("tenant", "integer")],
        [new SqlColumnDefinition("Id", "integer"), new SqlColumnDefinition("Total", "numeric")],
        "SELECT \"Id\", \"Total\" FROM \"Orders\" WHERE \"TenantId\" = tenant;");

    [Fact]
    public void PostgreSql_scalar()
        => Scalar.GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
        [
            """
            CREATE OR REPLACE FUNCTION "math"."add"(a integer, b integer)
            RETURNS integer
            LANGUAGE plpgsql
            AS $$
            BEGIN RETURN a + b; END;
            $$
            """,
        ]);

    [Fact]
    public void PostgreSql_table_declares_its_columns_quoted_and_defaults_to_sql()
        => Table.GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
        [
            """
            CREATE OR REPLACE FUNCTION "orders_for"(tenant integer)
            RETURNS TABLE ("Id" integer, "Total" numeric)
            LANGUAGE sql
            AS $$
            SELECT "Id", "Total" FROM "Orders" WHERE "TenantId" = tenant;
            $$
            """,
        ]);

    [Fact]
    public void PostgreSql_characteristics_follow_the_language()
        => FunctionDefinition.Scalar("f", null, "int", "SELECT 1", language: "sql", characteristics: "IMMUTABLE PARALLEL SAFE")
            .GetCreateSql(SqlDialect.PostgreSql)[0]
            .ShouldBe("CREATE OR REPLACE FUNCTION \"f\"()\nRETURNS int\nLANGUAGE sql\nIMMUTABLE PARALLEL SAFE\nAS $$\nSELECT 1\n$$");

    [Fact]
    public void PostgreSql_body_with_dollar_quotes_gets_another_tag()
        => FunctionDefinition.Scalar("f", null, "text", "SELECT $$x$$", language: "sql")
            .GetCreateSql(SqlDialect.PostgreSql)[0]
            .ShouldEndWith("AS $body$\nSELECT $$x$$\n$body$");

    [Fact]
    public void SqlServer_scalar()
        => FunctionDefinition.Scalar("add", Parameters, "int", "BEGIN RETURN @a + @b; END", "math")
            .GetCreateSql(SqlDialect.SqlServer)
            .ShouldBe(["CREATE OR ALTER FUNCTION [math].[add](@a integer, @b integer)\nRETURNS int\nAS\nBEGIN RETURN @a + @b; END"]);

    [Fact]
    public void SqlServer_scalar_takes_characteristics_as_a_with_clause()
        => FunctionDefinition.Scalar("f", null, "int", "BEGIN RETURN 1; END", characteristics: "SCHEMABINDING")
            .GetCreateSql(SqlDialect.SqlServer)[0]
            .ShouldBe("CREATE OR ALTER FUNCTION [f]()\nRETURNS int\nWITH SCHEMABINDING\nAS\nBEGIN RETURN 1; END");

    [Fact]
    public void SqlServer_table_is_an_inline_function_returning_the_query()
        => FunctionDefinition.Table(
                "orders_for",
                [new SqlParameterDefinition("tenant", "int")],
                [new SqlColumnDefinition("Id", "int")],
                "SELECT [Id] FROM [Orders] WHERE [TenantId] = @tenant;",
                "dbo")
            .GetCreateSql(SqlDialect.SqlServer)
            .ShouldBe(["CREATE OR ALTER FUNCTION [dbo].[orders_for](@tenant int)\nRETURNS TABLE\nAS\nRETURN (\nSELECT [Id] FROM [Orders] WHERE [TenantId] = @tenant\n)"]);

    [Fact]
    public void MySql_scalar_drops_and_creates_with_characteristics()
        => FunctionDefinition.Scalar("add", Parameters, "INT", "RETURN a + b", characteristics: "DETERMINISTIC")
            .GetCreateSql(SqlDialect.MySql)
            .ShouldBe(
            [
                "DROP FUNCTION IF EXISTS `add`",
                "CREATE FUNCTION `add`(a integer, b integer)\nRETURNS INT\nDETERMINISTIC\nRETURN a + b",
            ]);

    [Fact]
    public void MySql_scalar_without_characteristics()
        => FunctionDefinition.Scalar("one", null, "INT", "RETURN 1")
            .GetCreateSql(SqlDialect.MySql)[1]
            .ShouldBe("CREATE FUNCTION `one`()\nRETURNS INT\nRETURN 1");

    [Fact]
    public void MySql_refuses_table_functions_in_both_directions()
    {
        var create = Should.Throw<SqlFeatureNotSupportedException>(() => Table.GetCreateSql(SqlDialect.MySql));
        create.Feature.ShouldBe("Table-valued functions");
        create.Dialect.ShouldBe(SqlDialect.MySql);

        Should.Throw<SqlFeatureNotSupportedException>(() => Table.GetDropSql(SqlDialect.MySql));
    }

    [Fact]
    public void Sqlite_refuses_every_function()
    {
        Should.Throw<SqlFeatureNotSupportedException>(() => Scalar.GetCreateSql(SqlDialect.Sqlite)).Feature.ShouldBe("Scalar functions");
        Should.Throw<SqlFeatureNotSupportedException>(() => Table.GetCreateSql(SqlDialect.Sqlite)).Feature.ShouldBe("Table-valued functions");
        Should.Throw<SqlFeatureNotSupportedException>(() => Scalar.GetDropSql(SqlDialect.Sqlite));
        Should.Throw<SqlFeatureNotSupportedException>(() => FunctionDefinition.DropSql(SqlDialect.Sqlite, "f", null))
            .Message.ShouldContain("CreateFunction");
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "DROP FUNCTION IF EXISTS \"math\".\"add\"")]
    [InlineData(SqlDialect.SqlServer, "DROP FUNCTION IF EXISTS [math].[add]")]
    [InlineData(SqlDialect.MySql, "DROP FUNCTION IF EXISTS `math`.`add`")]
    public void Drop_is_by_name_and_if_exists(SqlDialect dialect, string expected)
        => Scalar.GetDropSql(dialect).ShouldBe([expected]);

    [Fact]
    public void Output_parameters_are_refused()
        => Should.Throw<ArgumentException>(() =>
                FunctionDefinition.Scalar("f", [new SqlParameterDefinition("x", "int", SqlParameterDirection.Out)], "int", "x"))
            .Message.ShouldContain("stored procedure");

    [Fact]
    public void A_table_function_needs_columns()
    {
        Should.Throw<ArgumentException>(() => FunctionDefinition.Table("f", null, [], "SELECT 1"));
        Should.Throw<ArgumentException>(() => FunctionDefinition.Table("f", null, [null!], "SELECT 1"));
        Should.Throw<ArgumentNullException>(() => FunctionDefinition.Table("f", null, null!, "SELECT 1"));
    }

    [Fact]
    public void A_scalar_function_needs_a_return_type()
        => Should.Throw<ArgumentException>(() => FunctionDefinition.Scalar("f", null, " ", "x"));

    [Fact]
    public void The_definitions_keep_what_they_were_given()
    {
        Scalar.Kind.ShouldBe(FunctionKind.Scalar);
        Scalar.Name.ShouldBe("add");
        Scalar.Schema.ShouldBe("math");
        Scalar.ReturnType.ShouldBe("integer");
        Scalar.Columns.ShouldBeEmpty();
        Scalar.Language.ShouldBe("plpgsql");
        Scalar.Characteristics.ShouldBeNull();
        Scalar.Parameters.Count.ShouldBe(2);

        Table.Kind.ShouldBe(FunctionKind.Table);
        Table.ReturnType.ShouldBeNull();
        Table.Columns.Select(c => c.Name).ShouldBe(["Id", "Total"]);
        Table.Language.ShouldBe("sql");
        Table.Body.ShouldEndWith("tenant;");
    }
}
