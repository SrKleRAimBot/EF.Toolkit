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
        drop[2].ShouldContain("WHERE polrelid = '\"shop\".\"Orders\"'::regclass");
        drop[2].ShouldContain("ALTER TABLE \"shop\".\"Orders\" DISABLE ROW LEVEL SECURITY;");
    }

    [Fact]
    public void PostgreSql_drop_in_the_default_schema_resolves_the_table_the_way_ALTER_TABLE_does()
        => Policy.GetDropSql(SqlDialect.PostgreSql)[2].ShouldContain("polrelid = '\"Orders\"'::regclass");

    [Theory]
    [InlineData("orders$tenant_isolation$archive")]
    [InlineData("orders$$archive")]
    [InlineData("orders$body$archive")]
    public void PostgreSql_drop_quotes_its_block_with_a_tag_the_table_name_cannot_close(string table)
    {
        var block = new TenantIsolationPolicy(table, "TenantId", "app.tenant_id", "integer", policyName: "iso")
            .GetDropSql(SqlDialect.PostgreSql)[2];

        block.ShouldStartWith("DO $");
        var tag = block[3..(block.IndexOf('$', 4) + 1)];
        var body = block[(3 + tag.Length)..^tag.Length];
        body.ShouldNotContain(tag);
        body.ShouldContain(table);
        block.ShouldEndWith(tag);
    }

    [Fact]
    public void PostgreSql_names_that_PostgreSQL_would_truncate_are_refused()
    {
        // 63 bytes on its own, so "_access" takes the pair past the limit and both would truncate to
        // the same name — the access policy's DROP IF EXISTS would then remove the tenant policy.
        var name = new string('p', 63);
        var policy = new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "integer", policyName: name);

        Should.Throw<ArgumentException>(() => policy.GetCreateSql(SqlDialect.PostgreSql))
            .Message.ShouldContain("truncates names past 63");
        Should.Throw<ArgumentException>(() => policy.GetDropSql(SqlDialect.PostgreSql));
    }

    [Fact]
    public void PostgreSql_counts_bytes_not_characters()
    {
        // 28 two-byte characters plus "_access" is exactly 63 bytes and allowed; one more character is not.
        new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "integer", policyName: new string('é', 28))
            .GetCreateSql(SqlDialect.PostgreSql).ShouldNotBeEmpty();
        Should.Throw<ArgumentException>(() => new TenantIsolationPolicy(
            "Orders", "TenantId", "app.tenant_id", "integer", policyName: new string('é', 29)).GetCreateSql(SqlDialect.PostgreSql));
    }

    [Fact]
    public void A_long_default_name_is_refused_too()
        => Should.Throw<ArgumentException>(() => new TenantIsolationPolicy(
            new string('t', 40), "TenantId", "app.tenant_id", "integer").GetCreateSql(SqlDialect.PostgreSql));

    [Fact]
    public void Without_the_base_grant_only_the_restrictive_policy_is_created_and_dropped()
    {
        var policy = new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "integer", grantAccess: false);

        policy.GrantsAccess.ShouldBeFalse();
        var create = policy.GetCreateSql(SqlDialect.PostgreSql);
        create.Count.ShouldBe(5);
        create.ShouldNotContain(sql => sql.StartsWith("CREATE POLICY \"Orders_tenant_isolation_access\"", StringComparison.Ordinal));

        // An earlier install with the grant left it behind; it is withdrawn, after the tenant policy is in place.
        create[^1].ShouldBe("DROP POLICY IF EXISTS \"Orders_tenant_isolation_access\" ON \"Orders\"");

        var drop = policy.GetDropSql(SqlDialect.PostgreSql);
        drop.Count.ShouldBe(2);
        drop.ShouldNotContain(sql => sql.Contains("_access", StringComparison.Ordinal));

        // A name only the restrictive policy carries may use the whole limit — and then no "_access" name is
        // dropped, since truncated to 63 bytes it would be the tenant policy's own.
        new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", "integer", policyName: new string('p', 63), grantAccess: false)
            .GetCreateSql(SqlDialect.PostgreSql).ShouldNotContain(sql => sql.Contains("_access", StringComparison.Ordinal));
    }

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
            RETURN SELECT 1 AS allowed WHERE @tenant = CAST(SESSION_CONTEXT(N'TenantId') AS int) AND (CAST(CAST(SESSION_CONTEXT(N'TenantId') AS int) AS sql_variant) = SESSION_CONTEXT(N'TenantId') OR (SQL_VARIANT_PROPERTY(SESSION_CONTEXT(N'TenantId'), 'BaseType') IN ('nvarchar', 'varchar', 'nchar', 'char') AND CAST(CAST(CAST(SESSION_CONTEXT(N'TenantId') AS int) AS nvarchar(4000)) AS varbinary(8000)) = CAST(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(4000)) AS varbinary(8000)) AND DATALENGTH(CAST(CAST(SESSION_CONTEXT(N'TenantId') AS int) AS nvarchar(4000))) = DATALENGTH(CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(4000)))))
            """,
            """
            CREATE SECURITY POLICY [sales].[Orders_tenant_isolation]
                ADD FILTER PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders],
                ADD BLOCK PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders] AFTER INSERT,
                ADD BLOCK PREDICATE [sales].[Orders_tenant_isolation_predicate]([TenantId]) ON [sales].[Orders] AFTER UPDATE
            WITH (STATE = ON)
            """,
        ]);

    [Theory]
    [InlineData("numeric(10,2)", "numeric")]
    [InlineData(" DECIMAL(12, 4)", "DECIMAL")]
    [InlineData("pg_catalog.numeric(10,2)", "pg_catalog.numeric")]
    [InlineData("varchar(8)", "varchar")]
    [InlineData("character varying (8)", "character varying")]
    [InlineData("timestamp(0) with time zone", "timestamp with time zone")]
    [InlineData("char(8)", "bpchar")]
    [InlineData("CHARACTER(8)", "bpchar")]
    [InlineData("pg_catalog.bpchar(8)", "pg_catalog.bpchar")]
    [InlineData("bit(3)", "varbit")]
    [InlineData("varchar(8)[]", "varchar[]")]
    [InlineData("character", "bpchar")]
    [InlineData("char", "bpchar")]
    [InlineData("pg_catalog.bit", "pg_catalog.varbit")]
    [InlineData("char[]", "bpchar[]")]
    public void PostgreSql_compares_a_constrained_cast_with_the_unconstrained_one(string storeType, string unconstrained)
    {
        var sql = new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", storeType).GetCreateSql(SqlDialect.PostgreSql)[3];

        sql.ShouldContain(
            $"AS {storeType.Trim()}) = CAST(NULLIF(current_setting('app.tenant_id', true), '') AS {unconstrained})");
        sql.ShouldNotContain("AS text)");
    }

    [Theory]
    [InlineData("integer")]
    [InlineData("uuid")]
    [InlineData("pg_catalog.timestamp")]
    public void PostgreSql_needs_no_second_comparison_for_a_type_without_modifiers(string storeType)
        => new TenantIsolationPolicy("Orders", "TenantId", "app.tenant_id", storeType).GetCreateSql(SqlDialect.PostgreSql)[3]
            .ShouldContain($"USING (\"TenantId\" = CAST(NULLIF(current_setting('app.tenant_id', true), '') AS {storeType}))\n");

    [Theory]
    [InlineData("varchar(10)")]
    [InlineData("NVARCHAR(64)")]
    [InlineData("sysname")]
    [InlineData("national character varying (20)")]
    public void SqlServer_compares_a_character_tenant_byte_for_byte_whatever_the_database_collation(string storeType)
    {
        var sql = new TenantIsolationPolicy("Orders", "TenantId", "TenantId", storeType).GetCreateSql(SqlDialect.SqlServer)[1];

        sql.ShouldContain("(@tenant nvarchar(max))");
        const string full = "(CASE WHEN SQL_VARIANT_PROPERTY(SESSION_CONTEXT(N'TenantId'), 'BaseType') IN ('varchar', 'char') "
            + "THEN CAST(CAST(SESSION_CONTEXT(N'TenantId') AS varchar(8000)) AS nvarchar(max)) "
            + "ELSE CAST(SESSION_CONTEXT(N'TenantId') AS nvarchar(4000)) END)";
        sql.ShouldContain($"AND CAST(@tenant AS varbinary(max)) = CAST({full} AS varbinary(max)) AND DATALENGTH(@tenant) = DATALENGTH({full})");
    }

    [Theory]
    [InlineData("nvarchar(max)")]
    [InlineData("varchar(10)")]
    public void SqlServer_never_converts_a_character_tenant_to_sql_variant(string storeType)
    {
        // nvarchar(max) cannot be converted to sql_variant at all, and for a string the variant comparison pads.
        var sql = new TenantIsolationPolicy("Orders", "TenantId", "TenantId", storeType).GetCreateSql(SqlDialect.SqlServer)[1];

        sql.ShouldNotContain("AS sql_variant)");
        sql.ShouldContain("(@tenant nvarchar(max))");
    }

    [Theory]
    [InlineData("int")]
    [InlineData("uniqueidentifier")]
    [InlineData("decimal(10,2)")]
    public void SqlServer_compares_other_tenants_by_value(string storeType)
        => new TenantIsolationPolicy("Orders", "TenantId", "TenantId", storeType).GetCreateSql(SqlDialect.SqlServer)[1]
            .ShouldNotContain("CAST(@tenant AS varbinary");

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
