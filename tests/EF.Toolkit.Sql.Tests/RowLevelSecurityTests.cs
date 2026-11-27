using EFToolkit.Sql.Security;

namespace EFToolkit.Sql.Tests;

public sealed class RowLevelSecurityTableTests
{
    [Fact]
    public void Enables_without_force_by_default()
        => new RowLevelSecurityTable("Orders").GetCreateSql(SqlDialect.PostgreSql)
            .ShouldBe(["ALTER TABLE \"Orders\" ENABLE ROW LEVEL SECURITY"]);

    [Fact]
    public void Force_applies_the_policies_to_the_owner_too()
        => new RowLevelSecurityTable("Orders", "shop", force: true).GetCreateSql(SqlDialect.PostgreSql).ShouldBe(
        [
            "ALTER TABLE \"shop\".\"Orders\" ENABLE ROW LEVEL SECURITY",
            "ALTER TABLE \"shop\".\"Orders\" FORCE ROW LEVEL SECURITY",
        ]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Drop_always_clears_force_before_disabling(bool force)
        => new RowLevelSecurityTable("Orders", force: force).GetDropSql(SqlDialect.PostgreSql).ShouldBe(
        [
            "ALTER TABLE \"Orders\" NO FORCE ROW LEVEL SECURITY",
            "ALTER TABLE \"Orders\" DISABLE ROW LEVEL SECURITY",
        ]);

    [Theory]
    [InlineData(SqlDialect.SqlServer, "SqlServerSecurityPolicy")]
    [InlineData(SqlDialect.MySql, "global query filter")]
    [InlineData(SqlDialect.Sqlite, "global query filter")]
    public void Other_engines_refuse_and_point_somewhere(SqlDialect dialect, string hint)
    {
        var table = new RowLevelSecurityTable("Orders");

        var ex = Should.Throw<SqlFeatureNotSupportedException>(() => table.GetCreateSql(dialect));
        ex.Dialect.ShouldBe(dialect);
        ex.Feature!.ShouldContain("Row-level security");
        ex.Message.ShouldContain(hint);
        Should.Throw<SqlFeatureNotSupportedException>(() => table.GetDropSql(dialect));
    }

    [Fact]
    public void Keeps_what_it_was_given()
    {
        var table = new RowLevelSecurityTable("Orders", "shop", true);
        table.Table.ShouldBe("Orders");
        table.Schema.ShouldBe("shop");
        table.Force.ShouldBeTrue();
        Should.Throw<ArgumentException>(() => new RowLevelSecurityTable(""));
    }
}

public sealed class RowLevelSecurityPolicyTests
{
    [Fact]
    public void Renders_every_clause()
        => new RowLevelSecurityPolicy(
                "orders_owner",
                "Orders",
                "\"OwnerId\" = current_user",
                "\"OwnerId\" = current_user",
                "shop",
                PolicyCommand.Update,
                PolicyKind.Restrictive,
                ["app_user", "public", "current_user"])
            .GetCreateSql(SqlDialect.PostgreSql)
            .ShouldBe(
            [
                "DROP POLICY IF EXISTS \"orders_owner\" ON \"shop\".\"Orders\"",
                """
                CREATE POLICY "orders_owner" ON "shop"."Orders"
                    AS RESTRICTIVE
                    FOR UPDATE
                    TO "app_user", PUBLIC, CURRENT_USER
                    USING ("OwnerId" = current_user)
                    WITH CHECK ("OwnerId" = current_user)
                """,
            ]);

    [Fact]
    public void Defaults_to_a_permissive_policy_for_everyone_and_every_command()
        => new RowLevelSecurityPolicy("p", "t", "true").GetCreateSql(SqlDialect.PostgreSql)[1].ShouldBe(
            "CREATE POLICY \"p\" ON \"t\"\n    AS PERMISSIVE\n    FOR ALL\n    TO PUBLIC\n    USING (true)");

    [Fact]
    public void An_insert_policy_has_only_with_check()
        => new RowLevelSecurityPolicy("p", "t", null, "x > 0", command: PolicyCommand.Insert)
            .GetCreateSql(SqlDialect.PostgreSql)[1]
            .ShouldBe("CREATE POLICY \"p\" ON \"t\"\n    AS PERMISSIVE\n    FOR INSERT\n    TO PUBLIC\n    WITH CHECK (x > 0)");

    [Fact]
    public void Role_names_with_quotes_are_escaped()
        => new RowLevelSecurityPolicy("p", "t", "true", roles: ["we\"ird"]).GetCreateSql(SqlDialect.PostgreSql)[1]
            .ShouldContain("TO \"we\"\"ird\"");

    [Fact]
    public void Drop_names_the_table()
        => new RowLevelSecurityPolicy("p", "t", "true", schema: "s").GetDropSql(SqlDialect.PostgreSql)
            .ShouldBe(["DROP POLICY IF EXISTS \"p\" ON \"s\".\"t\""]);

    [Theory]
    [InlineData(PolicyCommand.Insert, "x", "x")]
    [InlineData(PolicyCommand.Insert, null, null)]
    [InlineData(PolicyCommand.Select, "x", "x")]
    [InlineData(PolicyCommand.Select, null, null)]
    [InlineData(PolicyCommand.Delete, null, "x")]
    [InlineData(PolicyCommand.All, null, null)]
    [InlineData(PolicyCommand.Update, " ", "")]
    public void Combinations_PostgreSql_rejects_are_refused_up_front(PolicyCommand command, string? usingExpression, string? withCheck)
        => Should.Throw<ArgumentException>(() => new RowLevelSecurityPolicy("p", "t", usingExpression, withCheck, command: command));

    [Theory]
    [InlineData(PolicyCommand.All, "x", null)]
    [InlineData(PolicyCommand.All, null, "x")]
    [InlineData(PolicyCommand.Update, "x", "x")]
    [InlineData(PolicyCommand.Select, "x", null)]
    [InlineData(PolicyCommand.Delete, "x", null)]
    [InlineData(PolicyCommand.Insert, null, "x")]
    public void Combinations_PostgreSql_accepts_are_allowed(PolicyCommand command, string? usingExpression, string? withCheck)
        => new RowLevelSecurityPolicy("p", "t", usingExpression, withCheck, command: command).Command.ShouldBe(command);

    [Fact]
    public void Undefined_enums_and_blank_roles_are_refused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RowLevelSecurityPolicy("p", "t", "x", command: (PolicyCommand)9));
        Should.Throw<ArgumentOutOfRangeException>(() => new RowLevelSecurityPolicy("p", "t", "x", kind: (PolicyKind)9));
        Should.Throw<ArgumentException>(() => new RowLevelSecurityPolicy("p", "t", "x", roles: [" "]));
        Should.Throw<ArgumentException>(() => new RowLevelSecurityPolicy("", "t", "x"));
        Should.Throw<ArgumentException>(() => new RowLevelSecurityPolicy("p", "", "x"));
    }

    [Theory]
    [InlineData(SqlDialect.SqlServer)]
    [InlineData(SqlDialect.MySql)]
    [InlineData(SqlDialect.Sqlite)]
    public void Only_PostgreSql_renders_it(SqlDialect dialect)
    {
        var policy = new RowLevelSecurityPolicy("p", "t", "true");

        Should.Throw<SqlFeatureNotSupportedException>(() => policy.GetCreateSql(dialect)).Feature!.ShouldContain("CREATE POLICY");
        Should.Throw<SqlFeatureNotSupportedException>(() => policy.GetDropSql(dialect));
    }

    [Fact]
    public void Keeps_what_it_was_given()
    {
        var policy = new RowLevelSecurityPolicy("p", "t", " a ", " b ", "s", PolicyCommand.Update, PolicyKind.Restrictive, ["r"]);

        policy.Name.ShouldBe("p");
        policy.Table.ShouldBe("t");
        policy.Using.ShouldBe("a");
        policy.WithCheck.ShouldBe("b");
        policy.Schema.ShouldBe("s");
        policy.Kind.ShouldBe(PolicyKind.Restrictive);
        policy.Roles.ShouldBe(["r"]);
    }
}
