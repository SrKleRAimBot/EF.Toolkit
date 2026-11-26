using EFToolkit.Sql.Security;

namespace EFToolkit.Sql.Tests;

public sealed class SqlServerSecurityPolicyTests
{
    private static readonly SqlServerSecurityPolicy Policy = new(
        "OrdersPolicy",
        "Orders",
        "fn_orders",
        [new SqlServerPredicateParameter("TenantId", "int", "TenantId")],
        "@TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS int)",
        securitySchema: "security");

    [Fact]
    public void Creates_function_and_policy_after_dropping_the_policy()
        => Policy.GetCreateSql(SqlDialect.SqlServer).ShouldBe(
        [
            "DROP SECURITY POLICY IF EXISTS [security].[OrdersPolicy]",
            """
            CREATE OR ALTER FUNCTION [security].[fn_orders](@TenantId int)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS int)
            """,
            """
            CREATE SECURITY POLICY [security].[OrdersPolicy]
                ADD FILTER PREDICATE [security].[fn_orders]([TenantId]) ON [dbo].[Orders],
                ADD BLOCK PREDICATE [security].[fn_orders]([TenantId]) ON [dbo].[Orders] AFTER INSERT,
                ADD BLOCK PREDICATE [security].[fn_orders]([TenantId]) ON [dbo].[Orders] AFTER UPDATE
            WITH (STATE = ON)
            """,
        ]);

    [Fact]
    public void Drops_the_policy_before_the_function_it_binds()
        => Policy.GetDropSql(SqlDialect.SqlServer).ShouldBe(
        [
            "DROP SECURITY POLICY IF EXISTS [security].[OrdersPolicy]",
            "DROP FUNCTION IF EXISTS [security].[fn_orders]",
        ]);

    [Fact]
    public void Every_block_operation_and_several_columns()
    {
        var policy = new SqlServerSecurityPolicy(
            "p",
            "Docs",
            "fn",
            [new SqlServerPredicateParameter("@owner", "int", "Owner]Id"), new SqlServerPredicateParameter("dept", "int", "DeptId")],
            "@owner = 1 OR @dept = 2",
            schema: "app",
            filter: false,
            block: SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate
                | SqlServerBlockOperations.BeforeUpdate | SqlServerBlockOperations.BeforeDelete);

        policy.GetCreateSql(SqlDialect.SqlServer)[2].ShouldBe(
            """
            CREATE SECURITY POLICY [app].[p]
                ADD BLOCK PREDICATE [app].[fn]([Owner]]Id], [DeptId]) ON [app].[Docs] AFTER INSERT,
                ADD BLOCK PREDICATE [app].[fn]([Owner]]Id], [DeptId]) ON [app].[Docs] AFTER UPDATE,
                ADD BLOCK PREDICATE [app].[fn]([Owner]]Id], [DeptId]) ON [app].[Docs] BEFORE UPDATE,
                ADD BLOCK PREDICATE [app].[fn]([Owner]]Id], [DeptId]) ON [app].[Docs] BEFORE DELETE
            WITH (STATE = ON)
            """);
        policy.GetCreateSql(SqlDialect.SqlServer)[1].ShouldStartWith("CREATE OR ALTER FUNCTION [app].[fn](@owner int, @dept int)");
    }

    [Fact]
    public void Schema_defaults_to_dbo_and_security_schema_to_the_table_schema()
    {
        var policy = new SqlServerSecurityPolicy("p", "t", "fn", [new SqlServerPredicateParameter("a", "int", "A")], "@a = 1");

        policy.Schema.ShouldBe("dbo");
        policy.SecuritySchema.ShouldBe("dbo");
        policy.Filter.ShouldBeTrue();
        policy.Block.ShouldBe(SqlServerBlockOperations.AfterInsert | SqlServerBlockOperations.AfterUpdate);
        policy.Parameters.Single().Name.ShouldBe("a");
        policy.Predicate.ShouldBe("@a = 1");
        policy.FunctionName.ShouldBe("fn");
        policy.Name.ShouldBe("p");
        policy.Table.ShouldBe("t");
    }

    [Fact]
    public void A_policy_that_protects_nothing_is_refused()
        => Should.Throw<ArgumentException>(() => new SqlServerSecurityPolicy(
            "p", "t", "fn", [new SqlServerPredicateParameter("a", "int", "A")], "@a = 1",
            filter: false, block: SqlServerBlockOperations.None));

    [Fact]
    public void Invalid_parameters_are_refused()
    {
        Should.Throw<ArgumentException>(() => new SqlServerSecurityPolicy("p", "t", "fn", [], "@a = 1"));
        Should.Throw<ArgumentException>(() => new SqlServerSecurityPolicy("p", "t", "fn", [null!], "@a = 1"));
        Should.Throw<ArgumentException>(() => new SqlServerSecurityPolicy(
            "p", "t", "fn", [new SqlServerPredicateParameter("a", "int", "A"), new SqlServerPredicateParameter("A", "int", "B")], "@a = 1"));
        Should.Throw<ArgumentOutOfRangeException>(() => new SqlServerSecurityPolicy(
            "p", "t", "fn", [new SqlServerPredicateParameter("a", "int", "A")], "@a = 1", block: (SqlServerBlockOperations)64));
        Should.Throw<ArgumentException>(() => new SqlServerPredicateParameter("a b", "int", "A"));
        Should.Throw<ArgumentException>(() => new SqlServerPredicateParameter("a", "int", ""));
    }

    [Theory]
    [InlineData(SqlDialect.PostgreSql, "RowLevelSecurityPolicy")]
    [InlineData(SqlDialect.MySql, "global query filter")]
    [InlineData(SqlDialect.Sqlite, "global query filter")]
    public void Only_SqlServer_renders_it(SqlDialect dialect, string hint)
    {
        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => Policy.GetCreateSql(dialect));
        ex.Feature!.ShouldContain("SECURITY POLICY");
        ex.Message.ShouldContain(hint);
        Should.Throw<SqlFeatureNotSupportedException>(() => Policy.GetDropSql(dialect));
    }
}
