using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing.Tests;

public sealed class ResetSqlGeneratorTests
{
    private static readonly ResetTable Lines = new(null, "Lines");
    private static readonly ResetTable Orders = new("sales", "Orders");
    private static readonly ResetTable Odd = new(null, "Odd]\"`'Name");

    private static readonly ResetConnectionState Default = new(RestartIdentity: true, InTransaction: false);

    private static ResetScript Generate(DatabaseEngine engine, ResetPlan plan, ResetConnectionState? state = null)
        => ResetSqlGenerator.Generate(engine, plan, state ?? Default);

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("SqlServer")]
    [InlineData("MySql")]
    [InlineData("Sqlite")]
    public void An_empty_plan_runs_nothing(string engine)
        => Generate(Enum.Parse<DatabaseEngine>(engine), ResetPlan.Create([])).All.ShouldBeEmpty();

    [Fact]
    public void PostgreSql_truncates_every_table_in_one_statement()
    {
        var script = Generate(DatabaseEngine.PostgreSql, ResetPlan.Create([Lines, Orders]));

        script.Prologue.ShouldBeEmpty();
        script.Epilogue.ShouldBeEmpty();
        script.Body.ShouldBe(["TRUNCATE TABLE \"Lines\", \"sales\".\"Orders\" RESTART IDENTITY CASCADE;"]);
    }

    [Fact]
    public void PostgreSql_continues_identity_when_asked()
        => Generate(DatabaseEngine.PostgreSql, ResetPlan.Create([Lines]), Default with { RestartIdentity = false })
            .Body.ShouldBe(["TRUNCATE TABLE \"Lines\" CASCADE;"]);

    [Fact]
    public void PostgreSql_does_not_cascade_once_anything_is_excluded()
        => Generate(DatabaseEngine.PostgreSql, ResetPlan.Create([Lines], hasExclusions: true))
            .Body.ShouldBe(["TRUNCATE TABLE \"Lines\" RESTART IDENTITY;"]);

    [Fact]
    public void PostgreSql_is_the_same_inside_a_transaction()
        => Generate(DatabaseEngine.PostgreSql, ResetPlan.Create([Lines]), Default with { InTransaction = true })
            .Body.ShouldBe(["TRUNCATE TABLE \"Lines\" RESTART IDENTITY CASCADE;"]);

    [Fact]
    public void SqlServer_deletes_in_order_and_reseeds_from_the_table_itself()
    {
        var script = Generate(DatabaseEngine.SqlServer, ResetPlan.Create([Lines, Orders]));

        script.Prologue.ShouldBeEmpty();
        script.Epilogue.ShouldBeEmpty();
        script.Body.Count.ShouldBe(4);
        script.Body[0].ShouldBe("DELETE FROM [Lines];");
        script.Body[1].ShouldBe("DELETE FROM [sales].[Orders];");
        script.Body[3].ShouldBe(
            """
            IF OBJECTPROPERTY(OBJECT_ID(N'[sales].[Orders]'), 'TableHasIdentity') = 1
            BEGIN
                DECLARE @reseed BIGINT = IDENT_SEED(N'[sales].[Orders]');
                IF (SELECT last_value FROM sys.identity_columns WHERE object_id = OBJECT_ID(N'[sales].[Orders]')) IS NOT NULL
                    SET @reseed = @reseed - IDENT_INCR(N'[sales].[Orders]');
                DBCC CHECKIDENT(N'[sales].[Orders]', RESEED, @reseed) WITH NO_INFOMSGS;
            END;
            """,
            StringCompareShould.IgnoreLineEndings);
    }

    [Fact]
    public void SqlServer_suspends_constraints_only_on_cyclic_tables()
    {
        var script = Generate(DatabaseEngine.SqlServer, ResetPlan.Create([Lines, Orders], cyclicTables: [Orders]));

        script.Prologue.ShouldBe(["ALTER TABLE [sales].[Orders] NOCHECK CONSTRAINT ALL;"]);
        script.Epilogue.ShouldBe(["ALTER TABLE [sales].[Orders] WITH CHECK CHECK CONSTRAINT ALL;"]);
    }

    [Fact]
    public void SqlServer_skips_the_reseed_when_identity_continues()
        => Generate(DatabaseEngine.SqlServer, ResetPlan.Create([Lines]), Default with { RestartIdentity = false })
            .Body.ShouldBe(["DELETE FROM [Lines];"]);

    [Fact]
    public void MySql_truncates_with_checks_suspended_and_restored()
    {
        var script = Generate(DatabaseEngine.MySql, ResetPlan.Create([Lines, Orders]));

        script.Prologue.ShouldBe(["SET FOREIGN_KEY_CHECKS = 0;"]);
        script.Body.ShouldBe(["TRUNCATE TABLE `Lines`;", "TRUNCATE TABLE `sales`.`Orders`;"]);
        script.Epilogue.ShouldBe(["SET FOREIGN_KEY_CHECKS = 1;"]);
    }

    [Fact]
    public void MySql_leaves_checks_off_when_they_were_off()
    {
        var script = Generate(DatabaseEngine.MySql, ResetPlan.Create([Lines]), Default with { MySqlForeignKeyChecks = false });

        script.Prologue.ShouldBeEmpty();
        script.Epilogue.ShouldBeEmpty();
        script.Body.ShouldBe(["TRUNCATE TABLE `Lines`;"]);
    }

    [Fact]
    public void MySql_deletes_inside_a_transaction_because_truncate_would_commit_it()
        => Generate(DatabaseEngine.MySql, ResetPlan.Create([Lines]), Default with { InTransaction = true })
            .Body.ShouldBe(["DELETE FROM `Lines`;"]);

    [Fact]
    public void MySql_deletes_when_identity_continues()
        => Generate(DatabaseEngine.MySql, ResetPlan.Create([Lines]), Default with { RestartIdentity = false })
            .Body.ShouldBe(["DELETE FROM `Lines`;"]);

    [Fact]
    public void Sqlite_turns_foreign_keys_off_and_back_on_and_clears_the_sequence()
    {
        var script = Generate(
            DatabaseEngine.Sqlite,
            ResetPlan.Create([Lines, new ResetTable(null, "O'Brien")]),
            Default with { SqliteHasSequenceTable = true });

        script.Prologue.ShouldBe(["PRAGMA foreign_keys = OFF;"]);
        script.Body.ShouldBe(
        [
            "DELETE FROM \"Lines\";",
            "DELETE FROM \"O'Brien\";",
            "DELETE FROM \"sqlite_sequence\" WHERE \"name\" IN ('Lines', 'O''Brien');",
        ]);
        script.Epilogue.ShouldBe(["PRAGMA foreign_keys = ON;"]);
    }

    [Fact]
    public void Sqlite_does_not_name_a_sequence_table_that_does_not_exist()
        => Generate(DatabaseEngine.Sqlite, ResetPlan.Create([Lines]), Default with { SqliteHasSequenceTable = false })
            .Body.ShouldBe(["DELETE FROM \"Lines\";"]);

    [Fact]
    public void Sqlite_leaves_the_sequence_alone_when_identity_continues()
        => Generate(
                DatabaseEngine.Sqlite,
                ResetPlan.Create([Lines]),
                Default with { RestartIdentity = false, SqliteHasSequenceTable = true })
            .Body.ShouldBe(["DELETE FROM \"Lines\";"]);

    [Fact]
    public void Sqlite_leaves_foreign_keys_off_when_they_were_off()
    {
        var script = Generate(DatabaseEngine.Sqlite, ResetPlan.Create([Lines]), Default with { SqliteForeignKeysEnabled = false });

        script.Prologue.ShouldBeEmpty();
        script.Epilogue.ShouldBeEmpty();
    }

    [Fact]
    public void Sqlite_defers_foreign_keys_inside_a_transaction()
    {
        var script = Generate(DatabaseEngine.Sqlite, ResetPlan.Create([Lines]), Default with { InTransaction = true });

        script.Prologue.ShouldBe(["PRAGMA defer_foreign_keys = ON;"]);
        script.Epilogue.ShouldBe(["PRAGMA defer_foreign_keys = OFF;"]);
    }

    [Theory]
    [InlineData("PostgreSql", "\"Odd]\"\"`'Name\"")]
    [InlineData("SqlServer", "[Odd]]\"`'Name]")]
    [InlineData("MySql", "`Odd]\"``'Name`")]
    [InlineData("Sqlite", "\"Odd]\"\"`'Name\"")]
    public void Identifiers_are_quoted_with_the_engine_quote_doubled(string engine, string quoted)
        => Generate(Enum.Parse<DatabaseEngine>(engine), ResetPlan.Create([Odd]), Default with { RestartIdentity = false })
            .Body[0].ShouldContain(quoted);

    [Fact]
    public void SqlServer_literals_double_single_quotes()
        => Generate(DatabaseEngine.SqlServer, ResetPlan.Create([Odd])).Body[1]
            .ShouldContain("N'[Odd]]\"`''Name]'");

    // From real models on each provider, so the plan and the generator are checked together.

    [Fact]
    public void A_PostgreSQL_model_truncates_every_table()
    {
        using var context = Contexts.Npgsql();

        var sql = DatabaseResetExtensions.GenerateResetScript(context.Database).Body.ShouldHaveSingleItem();

        sql.ShouldStartWith("TRUNCATE TABLE ");
        sql.ShouldEndWith(" RESTART IDENTITY CASCADE;");
        foreach (var table in new[] { "Categories", "Customers", "OrderLines", "Orders", "Players", "Products", "Tags", "Teams" })
        {
            sql.ShouldContain($"\"{table}\"");
        }

        sql.ShouldNotContain("CustomerSummaries");
    }

    [Fact]
    public void A_SQL_Server_model_deletes_dependents_first_and_suspends_only_the_cycle()
    {
        using var context = Contexts.SqlServer();

        var script = DatabaseResetExtensions.GenerateResetScript(context.Database);
        var deletes = script.Body.Where(s => s.StartsWith("DELETE", StringComparison.Ordinal)).ToList();

        deletes.IndexOf("DELETE FROM [OrderLines];").ShouldBeLessThan(deletes.IndexOf("DELETE FROM [Orders];"));
        deletes.IndexOf("DELETE FROM [Orders];").ShouldBeLessThan(deletes.IndexOf("DELETE FROM [Customers];"));
        script.Prologue.Order().ShouldBe(
        [
            "ALTER TABLE [Categories] NOCHECK CONSTRAINT ALL;",
            "ALTER TABLE [Players] NOCHECK CONSTRAINT ALL;",
            "ALTER TABLE [Teams] NOCHECK CONSTRAINT ALL;",
        ]);
        script.Body.Count(s => s.Contains("DBCC CHECKIDENT", StringComparison.Ordinal)).ShouldBe(8);
    }

    [Fact]
    public void A_MySQL_model_truncates_each_table_outside_a_transaction_and_deletes_inside_one()
    {
        using var context = Contexts.MySql();

        DatabaseResetExtensions.GenerateResetScript(context.Database).Body
            .ShouldAllBe(s => s.StartsWith("TRUNCATE TABLE `", StringComparison.Ordinal));
        DatabaseResetExtensions.GenerateResetScript(context.Database, inTransaction: true).Body
            .ShouldAllBe(s => s.StartsWith("DELETE FROM `", StringComparison.Ordinal));
    }

    [Fact]
    public void Options_reach_the_generator()
    {
        using var context = Contexts.Npgsql();

        DatabaseResetExtensions.GenerateResetScript(context.Database, o =>
            {
                o.RestartIdentity = false;
                o.ExcludeEntity<Tag>();
            })
            .Body.ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(
                s => s.ShouldNotContain("RESTART IDENTITY"),
                s => s.ShouldNotContain("CASCADE"),
                s => s.ShouldNotContain("\"Tags\""));
    }

    [Theory]
    [InlineData("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSql")]
    [InlineData("Microsoft.EntityFrameworkCore.SqlServer", "SqlServer")]
    [InlineData("MySql.EntityFrameworkCore", "MySql")]
    [InlineData("Pomelo.EntityFrameworkCore.MySql", "MySql")]
    [InlineData("Microsoft.EntityFrameworkCore.Sqlite", "Sqlite")]
    public void The_engine_is_read_off_the_provider_name(string provider, string engine)
        => DatabaseEngines.Detect(provider).ShouldBe(Enum.Parse<DatabaseEngine>(engine));

    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.InMemory")]
    [InlineData(null)]
    public void An_unknown_provider_is_refused_by_name(string? provider)
        => Should.Throw<NotSupportedException>(() => DatabaseEngines.Detect(provider))
            .Message.ShouldContain(provider ?? "(none)");

    [Fact]
    public void Each_provider_reports_the_name_the_engine_is_detected_from()
    {
        using var npgsql = Contexts.Npgsql();
        using var sqlServer = Contexts.SqlServer();
        using var mySql = Contexts.MySql();
        using var sqlite = Contexts.Sqlite();

        DatabaseEngines.Detect(npgsql.Database.ProviderName).ShouldBe(DatabaseEngine.PostgreSql);
        DatabaseEngines.Detect(sqlServer.Database.ProviderName).ShouldBe(DatabaseEngine.SqlServer);
        DatabaseEngines.Detect(mySql.Database.ProviderName).ShouldBe(DatabaseEngine.MySql);
        DatabaseEngines.Detect(sqlite.Database.ProviderName).ShouldBe(DatabaseEngine.Sqlite);
    }
}
