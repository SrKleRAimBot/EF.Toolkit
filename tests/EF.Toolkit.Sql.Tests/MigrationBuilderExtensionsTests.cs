using EFToolkit.Sql.Definitions;
using EFToolkit.Sql.Security;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFToolkit.Sql.Tests;

public sealed class MigrationBuilderExtensionsTests
{
    private const string PostgreSql = "Npgsql.EntityFrameworkCore.PostgreSQL";
    private const string SqlServer = "Microsoft.EntityFrameworkCore.SqlServer";
    private const string MySql = "Pomelo.EntityFrameworkCore.MySql";
    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    private static List<string> Sql(MigrationBuilder builder)
    {
        builder.Operations.ShouldAllBe(o => o is SqlOperation);
        builder.Operations.Cast<SqlOperation>().ShouldAllBe(o => !o.SuppressTransaction);
        return builder.Operations.Cast<SqlOperation>().Select(o => Unwrap(builder, o.Sql)).ToList();
    }

    // SQL Server statements arrive wrapped in EXEC(N'...') for idempotent scripts; the assertions
    // below are about the statement inside, and the wrapping has tests of its own.
    private static string Unwrap(MigrationBuilder builder, string sql)
    {
        if (builder.ActiveProvider != SqlServer)
        {
            return sql;
        }

        sql.ShouldStartWith("EXEC(N'");
        sql.ShouldEndWith("')");
        return sql["EXEC(N'".Length..^"')".Length].Replace("''", "'", StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_statements_are_wrapped_in_EXEC_so_idempotent_scripts_keep_them_first_in_their_batch()
    {
        // `migrations script --idempotent` puts each operation inside IF NOT EXISTS (...) BEGIN ... END,
        // where CREATE OR ALTER VIEW is not first in its batch and fails. EXEC gives it a batch of its own.
        var builder = new MigrationBuilder(SqlServer);
        builder.CreateView(new ViewDefinition("v", "SELECT N'it''s' AS x"));

        var sql = ((SqlOperation)builder.Operations.Single()).Sql;
        sql.ShouldBe("EXEC(N'CREATE OR ALTER VIEW [v] AS\nSELECT N''it''''s'' AS x')");
    }

    [Fact]
    public void Other_engines_are_not_wrapped()
    {
        var builder = new MigrationBuilder(PostgreSql);
        builder.CreateView(new ViewDefinition("v", "SELECT 1"));

        ((SqlOperation)builder.Operations.Single()).Sql.ShouldNotStartWith("EXEC");
    }

    [Fact]
    public void CreateView_adds_one_operation_per_statement()
    {
        var builder = new MigrationBuilder(Sqlite);

        builder.CreateView("v", "SELECT 1").ShouldBeSameAs(builder);

        Sql(builder).ShouldBe(["DROP VIEW IF EXISTS \"v\"", "CREATE VIEW \"v\" AS\nSELECT 1"]);
    }

    [Fact]
    public void CreateView_renders_for_the_active_provider()
    {
        var builder = new MigrationBuilder(SqlServer);

        builder.CreateView(new ViewDefinition("v", "SELECT 1", "rpt"));

        Sql(builder).ShouldBe(["CREATE OR ALTER VIEW [rpt].[v] AS\nSELECT 1"]);
    }

    [Fact]
    public void DropView()
    {
        var builder = new MigrationBuilder(MySql);
        builder.DropView("v", "db");
        Sql(builder).ShouldBe(["DROP VIEW IF EXISTS `db`.`v`"]);
    }

    [Fact]
    public void Materialized_views()
    {
        var builder = new MigrationBuilder(PostgreSql);

        builder.CreateMaterializedView("m", "SELECT 1", withNoData: true)
            .CreateMaterializedView(new MaterializedViewDefinition("n", "SELECT 2"))
            .DropMaterializedView("m");

        Sql(builder).ShouldBe(
        [
            "DROP MATERIALIZED VIEW IF EXISTS \"m\"",
            "CREATE MATERIALIZED VIEW \"m\" AS\nSELECT 1\nWITH NO DATA",
            "DROP MATERIALIZED VIEW IF EXISTS \"n\"",
            "CREATE MATERIALIZED VIEW \"n\" AS\nSELECT 2",
            "DROP MATERIALIZED VIEW IF EXISTS \"m\"",
        ]);
    }

    [Fact]
    public void Stored_procedures_on_MySql_are_two_operations_without_delimiter()
    {
        var builder = new MigrationBuilder(MySql);

        builder.CreateStoredProcedure(new StoredProcedureDefinition("p", [new SqlParameterDefinition("a", "INT")], "BEGIN SELECT a; END"))
            .DropStoredProcedure("p");

        Sql(builder).ShouldBe(
        [
            "DROP PROCEDURE IF EXISTS `p`",
            "CREATE PROCEDURE `p`(IN a INT)\nBEGIN SELECT a; END",
            "DROP PROCEDURE IF EXISTS `p`",
        ]);
    }

    [Fact]
    public void Functions()
    {
        var builder = new MigrationBuilder(SqlServer);

        builder.CreateFunction(FunctionDefinition.Scalar("f", null, "int", "BEGIN RETURN 1; END")).DropFunction("f", "dbo");

        Sql(builder).ShouldBe(["CREATE OR ALTER FUNCTION [f]()\nRETURNS int\nAS\nBEGIN RETURN 1; END", "DROP FUNCTION IF EXISTS [dbo].[f]"]);
    }

    [Fact]
    public void Row_level_security_and_policies()
    {
        var builder = new MigrationBuilder(PostgreSql);
        var policy = new RowLevelSecurityPolicy("p", "t", "true");

        builder.EnableRowLevelSecurity("t", force: true)
            .CreatePolicy(policy)
            .DropPolicy(policy)
            .DropPolicy("q", "t", "s")
            .DisableRowLevelSecurity("t");

        Sql(builder).ShouldBe(
        [
            "ALTER TABLE \"t\" ENABLE ROW LEVEL SECURITY",
            "ALTER TABLE \"t\" FORCE ROW LEVEL SECURITY",
            "DROP POLICY IF EXISTS \"p\" ON \"t\"",
            "CREATE POLICY \"p\" ON \"t\"\n    AS PERMISSIVE\n    FOR ALL\n    TO PUBLIC\n    USING (true)",
            "DROP POLICY IF EXISTS \"p\" ON \"t\"",
            "DROP POLICY IF EXISTS \"q\" ON \"s\".\"t\"",
            "ALTER TABLE \"t\" NO FORCE ROW LEVEL SECURITY",
            "ALTER TABLE \"t\" DISABLE ROW LEVEL SECURITY",
        ]);
    }

    [Fact]
    public void SqlServer_security_policies()
    {
        var builder = new MigrationBuilder(SqlServer);
        var policy = new SqlServerSecurityPolicy("p", "t", "fn", [new SqlServerPredicateParameter("a", "int", "A")], "@a = 1");

        builder.CreatePolicy(policy).DropPolicy(policy);

        var sql = Sql(builder);
        sql.Count.ShouldBe(5);
        sql[0].ShouldBe("DROP SECURITY POLICY IF EXISTS [dbo].[p]");
        sql[1].ShouldStartWith("CREATE OR ALTER FUNCTION [dbo].[fn](@a int)");
        sql[2].ShouldStartWith("CREATE SECURITY POLICY [dbo].[p]");
        sql[3].ShouldBe("DROP SECURITY POLICY IF EXISTS [dbo].[p]");
        sql[4].ShouldBe("DROP FUNCTION IF EXISTS [dbo].[fn]");
    }

    [Theory]
    [InlineData(PostgreSql, 6, 3)]
    [InlineData(SqlServer, 3, 2)]
    public void Tenant_isolation(string provider, int createCount, int dropCount)
    {
        var policy = new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "int");

        var create = new MigrationBuilder(provider);
        create.CreateTenantIsolation(policy);
        Sql(create).Count.ShouldBe(createCount);
        Sql(create).ShouldBe(policy.GetCreateSql(provider == PostgreSql ? SqlDialect.PostgreSql : SqlDialect.SqlServer));

        var drop = new MigrationBuilder(provider);
        drop.DropTenantIsolation(policy);
        Sql(drop).Count.ShouldBe(dropCount);
    }

    [Fact]
    public void Any_database_object()
    {
        var builder = new MigrationBuilder(PostgreSql);
        var view = new ViewDefinition("v", "SELECT 1");

        builder.CreateDatabaseObject(view).DropDatabaseObject(view);

        Sql(builder).ShouldBe(["CREATE OR REPLACE VIEW \"v\" AS\nSELECT 1", "DROP VIEW IF EXISTS \"v\""]);
    }

    [Fact]
    public void A_refusal_adds_nothing()
    {
        var builder = new MigrationBuilder(Sqlite);

        Should.Throw<SqlFeatureNotSupportedException>(() =>
            builder.CreateTenantIsolation(new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "int")));
        Should.Throw<SqlFeatureNotSupportedException>(() => builder.CreateStoredProcedure(new StoredProcedureDefinition("p", null, "x")));
        Should.Throw<SqlFeatureNotSupportedException>(() => builder.DropStoredProcedure("p"));
        Should.Throw<SqlFeatureNotSupportedException>(() => builder.DropFunction("f"));
        Should.Throw<SqlFeatureNotSupportedException>(() => builder.CreateMaterializedView("m", "SELECT 1"));
        Should.Throw<SqlFeatureNotSupportedException>(() => builder.EnableRowLevelSecurity("t"));

        builder.Operations.ShouldBeEmpty();
    }

    [Fact]
    public void An_unknown_provider_is_refused()
    {
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.InMemory");

        Should.Throw<SqlFeatureNotSupportedException>(() => builder.CreateView("v", "SELECT 1"))
            .ProviderName.ShouldBe("Microsoft.EntityFrameworkCore.InMemory");
        Should.Throw<SqlFeatureNotSupportedException>(() => new MigrationBuilder(null).DropView("v"));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => ((MigrationBuilder)null!).DropView("v"));
        Should.Throw<ArgumentNullException>(() => new MigrationBuilder(PostgreSql).CreateDatabaseObject(null!));
        Should.Throw<ArgumentNullException>(() => new MigrationBuilder(PostgreSql).DropDatabaseObject(null!));
    }
}
