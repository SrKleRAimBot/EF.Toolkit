using EFToolkit.Sql.Security;

namespace EFToolkit.Sql.Tests;

public sealed class TenantIsolationPolicyTests
{
    private static readonly TenantIsolationPolicy Policy = new("Orders", "TenantId", "app.tenant_id", "integer");

    [Fact]
    public void PostgreSql_enables_forces_and_creates_a_restrictive_policy_over_a_base_grant()
        => Policy.GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
        [
            "ALTER TABLE \"Orders\" ENABLE ROW LEVEL SECURITY",
            "ALTER TABLE \"Orders\" FORCE ROW LEVEL SECURITY",
            "DROP POLICY IF EXISTS \"Orders_tenant_isolation\" ON \"Orders\"",
            """
            CREATE POLICY "Orders_tenant_isolation" ON "Orders"
                AS RESTRICTIVE
                FOR ALL
                TO PUBLIC
                USING ("TenantId" = CAST(NULLIF(current_setting('app.tenant_id', true), '') AS integer))
                WITH CHECK ("TenantId" = CAST(NULLIF(current_setting('app.tenant_id', true), '') AS integer))
            """,
            "DROP POLICY IF EXISTS \"Orders_tenant_isolation_access\" ON \"Orders\"",
            """
            CREATE POLICY "Orders_tenant_isolation_access" ON "Orders"
                AS PERMISSIVE
                FOR ALL
                TO PUBLIC
                USING (true)
                WITH CHECK (true)
            """,
        ]);

    [Fact]
    public void PostgreSql_drop_removes_both_policies_and_turns_rls_off_only_if_no_other_policy_remains()
    {
        var drop = new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "uuid", "shop", "iso")
            .GetDropSql(SqlDialect.PostgreSql);

        drop.Count.ShouldBe(3);
        drop[0].ShouldBe("DROP POLICY IF EXISTS \"iso_access\" ON \"shop\".\"Orders\"");
        drop[1].ShouldBe("DROP POLICY IF EXISTS \"iso\" ON \"shop\".\"Orders\"");
        drop[2].ShouldContain("IF NOT EXISTS (");
        drop[2].ShouldContain("WHERE schemaname = 'shop' AND tablename = 'Orders'");
        drop[2].ShouldContain("ALTER TABLE \"shop\".\"Orders\" DISABLE ROW LEVEL SECURITY;");
    }

    [Fact]
    public void PostgreSql_drop_in_the_default_schema_checks_the_current_one()
        => Policy.GetDropSql(SqlDialect.PostgreSql)[2].ShouldContain("schemaname = current_schema()");

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("app.")]
    [InlineData("app.tenant-id")]
    [InlineData("app.tenant'; DROP TABLE x; --")]
    public void PostgreSql_requires_a_dotted_custom_setting_name(string key)
        => Should.Throw<ArgumentException>(() =>
            new TenantIsolationPolicy("Orders", "TenantId", key, "integer").GetCreateSql(SqlDialect.PostgreSql));

    [Fact]
    public void SqlServer_creates_a_predicate_function_and_policy_over_session_context()
        => new TenantIsolationPolicy("Orders", "TenantId", "TenantId", "int", "sales").GetCreateSql(SqlDialect.SqlServer).ShouldBe(
        [
            "DROP SECURITY POLICY IF EXISTS [sales].[Orders_tenant_isolation]",
            """
            CREATE OR ALTER FUNCTION [sales].[Orders_tenant_isolation_predicate](@tenant int)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed WHERE @tenant = CAST(SESSION_CONTEXT(N'TenantId') AS int)
            """,
            """
            CREATE SECURITY POLICY [sales].[Orders_tenant_isolation]
                ADD FILTER PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders],
                ADD BLOCK PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders] AFTER INSERT,
                ADD BLOCK PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders] AFTER UPDATE
            WITH (STATE = ON)
            """,
        ]);

    [Fact]
    public void SqlServer_escapes_the_key_literal()
        => new TenantIsolationPolicy("Orders", "TenantId", "o'key", "int").GetCreateSql(SqlDialect.SqlServer)[1]
            .ShouldContain("SESSION_CONTEXT(N'o''key')");

    [Fact]
    public void SqlServer_drop_removes_policy_then_function()
        => Policy.GetDropSql(SqlDialect.SqlServer).ShouldBe(
        [
            "DROP SECURITY POLICY IF EXISTS [dbo].[Orders_tenant_isolation]",
            "DROP FUNCTION IF EXISTS [dbo].[Orders_tenant_isolation_predicate]",
        ]);

    [Fact]
    public void SqlServer_refuses_keys_longer_than_sysname()
        => Should.Throw<ArgumentException>(() =>
            new TenantIsolationPolicy("Orders", "TenantId", new string('k', 129), "int").GetCreateSql(SqlDialect.SqlServer));

    [Theory]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Sqlite)]
    public void Engines_without_rls_refuse(SqlDialect dialect)
    {
        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => Policy.GetCreateSql(dialect));
        ex.Feature.ShouldBe("Tenant isolation (row-level security)");
        ex.Dialect.ShouldBe(dialect);
        Should.Throw<SqlFeatureNotSupportedException>(() => Policy.GetDropSql(dialect));
    }

    [Fact]
    public void Keeps_what_it_was_given()
    {
        var policy = new TenantIsolationPolicy("Orders", "TenantId", "app.t", " integer ", "s", "named");

        policy.Table.ShouldBe("Orders");
        policy.TenantColumn.ShouldBe("TenantId");
        policy.SessionKey.ShouldBe("app.t");
        policy.ColumnStoreType.ShouldBe("integer");
        policy.Schema.ShouldBe("s");
        policy.PolicyName.ShouldBe("named");
    }

    [Fact]
    public void Blank_arguments_are_refused()
    {
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy("", "c", "a.b", "int"));
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy("t", "", "a.b", "int"));
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy("t", "c", "", "int"));
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy("t", "c", "a.b", ""));
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy("t", "c", "a.b", "int", policyName: " "));
    }
}
