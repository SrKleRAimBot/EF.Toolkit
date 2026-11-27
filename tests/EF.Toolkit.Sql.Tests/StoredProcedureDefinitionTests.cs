using EFToolkit.Sql.Definitions;

namespace EFToolkit.Sql.Tests;

public sealed class StoredProcedureDefinitionTests
{
    private static readonly SqlParameterDefinition[] Parameters =
    [
        new("tenant_id", "integer"),
        new("total", "numeric(10,2)", SqlParameterDirection.Out),
        new("counter", "integer", SqlParameterDirection.InOut),
    ];

    [Fact]
    public void PostgreSql_quotes_a_reserved_parameter_name_in_the_definition_as_calls_do()
    {
        var procedure = new StoredProcedureDefinition(
            "report", [new SqlParameterDefinition("from", "int"), new SqlParameterDefinition("tenantId", "int")], "BEGIN END;");

        procedure.GetCreateSql(SqlDialect.PostgreSql)[0].ShouldContain("(IN \"from\" int, IN \"tenantid\" int)");
        procedure.GetCreateSql(SqlDialect.MySql)[1].ShouldContain("IN `from` int");
    }

    [Fact]
    public void PostgreSql_creates_or_replaces_with_directions_and_dollar_quoting()
        => new StoredProcedureDefinition("close_month", Parameters, "BEGIN\n  total := 0;\nEND;", "billing")
            .GetCreateSql(SqlDialect.PostgreSql)
            .ShouldBe(
            [
                """
                CREATE OR REPLACE PROCEDURE "billing"."close_month"(IN "tenant_id" integer, OUT "total" numeric(10,2), INOUT "counter" integer)
                LANGUAGE plpgsql
                AS $$
                BEGIN
                  total := 0;
                END;
                $$
                """,
            ]);

    [Fact]
    public void PostgreSql_picks_a_dollar_tag_the_body_does_not_contain()
        => new StoredProcedureDefinition("p", null, "BEGIN EXECUTE $$SELECT 1$$; END;")
            .GetCreateSql(SqlDialect.PostgreSql)[0]
            .ShouldBe("CREATE OR REPLACE PROCEDURE \"p\"()\nLANGUAGE plpgsql\nAS $body$\nBEGIN EXECUTE $$SELECT 1$$; END;\n$body$");

    [Fact]
    public void PostgreSql_honours_the_language()
        => new StoredProcedureDefinition("p", null, "INSERT INTO t VALUES (1)", language: "sql")
            .GetCreateSql(SqlDialect.PostgreSql)[0]
            .ShouldContain("\nLANGUAGE sql\n");

    [Fact]
    public void SqlServer_creates_or_alters_with_output_parameters()
        => new StoredProcedureDefinition("close_month", Parameters, "BEGIN\n  SET @total = 0;\nEND", "billing")
            .GetCreateSql(SqlDialect.SqlServer)
            .ShouldBe(
            [
                """
                CREATE OR ALTER PROCEDURE [billing].[close_month]
                    @tenant_id integer,
                    @total numeric(10,2) OUTPUT,
                    @counter integer OUTPUT
                AS
                BEGIN
                  SET @total = 0;
                END
                """,
            ]);

    [Fact]
    public void SqlServer_without_parameters_has_no_parameter_list()
        => new StoredProcedureDefinition("p", [], "SELECT 1")
            .GetCreateSql(SqlDialect.SqlServer)
            .ShouldBe(["CREATE OR ALTER PROCEDURE [p]\nAS\nSELECT 1"]);

    [Fact]
    public void MySql_drops_and_creates_as_separate_statements_without_delimiter()
    {
        var sql = new StoredProcedureDefinition("close_month", Parameters, "BEGIN\n  SET total = 0;\nEND")
            .GetCreateSql(SqlDialect.MySql);

        sql.ShouldBe(
        [
            "DROP PROCEDURE IF EXISTS `close_month`",
            "CREATE PROCEDURE `close_month`(IN `tenant_id` integer, OUT `total` numeric(10,2), INOUT `counter` integer)\nBEGIN\n  SET total = 0;\nEND",
        ]);
        sql.ShouldAllBe(s => !s.Contains("DELIMITER"));
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "DROP PROCEDURE IF EXISTS \"billing\".\"close_month\"()")]
    [InlineData(SqlDialect.SqlServer, "DROP PROCEDURE IF EXISTS [billing].[close_month]")]
    [InlineData(SqlDialect.MySql, "DROP PROCEDURE IF EXISTS `billing`.`close_month`")]
    public void Drop_is_by_name_and_if_exists(SqlDialect dialect, string expected)
        => new StoredProcedureDefinition("close_month", null, "x", "billing").GetDropSql(dialect).ShouldBe([expected]);

    [Fact]
    public void PostgreSql_drops_by_its_whole_signature_modes_included()
        // OUT arguments are part of a procedure's identity, so every argument is listed with its mode.
        => new StoredProcedureDefinition("close_month", Parameters, "x", "billing").GetDropSql(SqlDialect.PostgreSql)
            .ShouldBe(["DROP PROCEDURE IF EXISTS \"billing\".\"close_month\"(IN integer, OUT numeric(10,2), INOUT integer)"]);

    [Fact]
    public void Sqlite_refuses_both_directions()
    {
        var procedure = new StoredProcedureDefinition("p", null, "x");

        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => procedure.GetCreateSql(SqlDialect.Sqlite));
        ex.Feature.ShouldBe("Stored procedures");
        ex.Dialect.ShouldBe(SqlDialect.Sqlite);
        ex.Message.ShouldContain("SQLite");
        Should.Throw<SqlFeatureNotSupportedException>(() => procedure.GetDropSql(SqlDialect.Sqlite));
    }

    [Fact]
    public void The_parameter_list_is_copied_so_the_definition_stays_immutable()
    {
        var list = new List<SqlParameterDefinition> { new("a", "int") };
        var procedure = new StoredProcedureDefinition("p", list, "x");

        list.Add(new SqlParameterDefinition("b", "int"));

        procedure.Parameters.Count.ShouldBe(1);
    }

    [Fact]
    public void The_definition_keeps_what_it_was_given()
    {
        var procedure = new StoredProcedureDefinition("p", Parameters, "  body  ", "s", "sql");

        procedure.Name.ShouldBe("p");
        procedure.Schema.ShouldBe("s");
        procedure.Body.ShouldBe("body");
        procedure.Language.ShouldBe("sql");
        procedure.Parameters.ShouldBe(Parameters);
    }

    [Fact]
    public void Duplicate_and_null_parameters_are_refused()
    {
        Should.Throw<ArgumentException>(() => new StoredProcedureDefinition("p", [new("a", "int"), new("A", "int")], "x"));
        Should.Throw<ArgumentException>(() => new StoredProcedureDefinition("p", [null!], "x"));
    }

    [Fact]
    public void A_language_that_is_not_an_identifier_is_refused()
        => Should.Throw<ArgumentException>(() => new StoredProcedureDefinition("p", null, "x", language: "plpgsql; DROP"));

    [Fact]
    public void Blank_names_and_bodies_are_refused()
    {
        Should.Throw<ArgumentException>(() => new StoredProcedureDefinition("", null, "x"));
        Should.Throw<ArgumentException>(() => new StoredProcedureDefinition("p", null, " "));
    }
}

public sealed class SqlParameterDefinitionTests
{
    [Fact]
    public void A_leading_at_sign_is_dropped()
        => new SqlParameterDefinition("@tenant", "int").Name.ShouldBe("tenant");

    [Fact]
    public void Defaults_to_an_input_parameter()
    {
        var parameter = new SqlParameterDefinition("tenant", "  int  ");

        parameter.Direction.ShouldBe(SqlParameterDirection.In);
        parameter.StoreType.ShouldBe("int");
    }

    [Theory]
    [InlineData("Tenant Id")]
    [InlineData("\"quoted\"")]
    [InlineData("1st")]
    public void Names_that_cannot_appear_bare_are_refused(string name)
        => Should.Throw<ArgumentException>(() => new SqlParameterDefinition(name, "int"));

    [Fact]
    public void A_blank_type_or_undefined_direction_is_refused()
    {
        Should.Throw<ArgumentException>(() => new SqlParameterDefinition("a", " "));
        Should.Throw<ArgumentOutOfRangeException>(() => new SqlParameterDefinition("a", "int", (SqlParameterDirection)9));
    }

    [Fact]
    public void Columns_keep_their_name_and_trimmed_type()
    {
        var column = new SqlColumnDefinition("Id", " integer ");

        column.Name.ShouldBe("Id");
        column.StoreType.ShouldBe("integer");
        Should.Throw<ArgumentException>(() => new SqlColumnDefinition("", "int"));
        Should.Throw<ArgumentException>(() => new SqlColumnDefinition("Id", ""));
    }
}
