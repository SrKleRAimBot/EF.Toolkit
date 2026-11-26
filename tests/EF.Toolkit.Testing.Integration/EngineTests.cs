using System.Data.Common;
using System.Globalization;
using EFToolkit.Testing.Integration.Infrastructure;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing.Integration;

/// <summary>Every feature that sends engine-specific SQL, against the engine itself.</summary>
public abstract class EngineTests(EngineFixture fixture) : IAsyncLifetime
{
    protected EngineFixture Fixture { get; } = fixture;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (Fixture.SkipReason is null)
        {
            await Fixture.Database.ResetAsync(Ct);
        }
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Creates a table the model does not map, with a foreign key into Customers and one row, runs the
    ///     reset expecting <typeparamref name="TException" />, and checks nothing was lost.
    /// </summary>
    protected async Task AssertUnmappedReferenceStopsResetAsync<TException>(string createTable, string dropTable)
        where TException : Exception
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(createTable, Ct);
        try
        {
            var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO Unmapped (Id, CustomerId) VALUES (1, {0})", [customer], Ct);

            await Should.ThrowAsync<TException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

            (await ScalarAsync(context, "SELECT COUNT(*) FROM Unmapped")).ShouldBe(1);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(dropTable, Ct);
        }
    }

    /// <summary>
    ///     Makes the delete of Customers fail with a trigger — after the tables ahead of it are already
    ///     emptied — and checks the reset rolled every one of them back and restored its settings.
    /// </summary>
    protected async Task AssertPartialFailureChangesNothingAsync(
        string createTrigger, string dropTrigger, bool inTransaction, Action<ResetDataOptions>? configure = null)
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(createTrigger, Ct);
        try
        {
            await using (var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null)
            {
                await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(configure, Ct));
                if (transaction is not null)
                {
                    // Some engines end the caller's transaction on the error itself; commit what is left.
                    try
                    {
                        await transaction.CommitAsync(Ct);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (DbException)
                    {
                    }
                }
            }

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(dropTrigger, Ct);
        }
    }

    private void SkipIfUnavailable() => Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");

    protected static async Task<long> ScalarAsync(DbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Reset_empties_chains_self_references_and_cycles()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
    }

    [Fact]
    public async Task Reset_restarts_identity_on_used_and_never_used_tables()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(cancellationToken: Ct);

        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBe(1);

        // Categories has not had a row since the database was created.
        (await context.SeedOneAsync(new Category { Name = "First" }, Ct)).Id.ShouldBe(1);

        await context.Database.ResetDataAsync(cancellationToken: Ct);
        (await context.SeedOneAsync(new Category { Name = "Again" }, Ct)).Id.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_continues_identity_when_asked()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.SeedAsync(ShopData.Customer(), ShopData.Customer());

        await context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct);

        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task Reset_keeps_excluded_tables()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        await context.Database.ResetDataAsync(o => o.ExcludeEntity<Product>().ExcludeTable("Tags"), Ct);

        var counts = await ShopData.CountsAsync(context);
        counts["Products"].ShouldBe(2);
        counts["Tags"].ShouldBe(1);
        counts["OrderLines"].ShouldBe(0);
        counts["Teams"].ShouldBe(0);
    }

    [Fact]
    public async Task Reset_runs_synchronously_too()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        context.Database.ResetData();

        (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        (await context.SeedOneAsync(ShopData.Customer(), Ct)).Id.ShouldBe(1);
    }

    [Fact]
    public async Task Reset_leaves_foreign_keys_enforced()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ResetDataAsync(cancellationToken: Ct);

        context.Add(new Player { Name = "Orphan", TeamId = 999 });
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Ct));

        await AssertConstraintsRestoredAsync(context);
    }

    /// <summary>Engine-specific evidence that the reset put everything back.</summary>
    protected virtual Task AssertConstraintsRestoredAsync(ShopContext context) => Task.CompletedTask;

    [Fact]
    public async Task An_isolated_scope_shares_one_transaction_and_rolls_it_back()
    {
        SkipIfUnavailable();

        await using (var scope = await Fixture.Database.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            var arrange = scope.CreateContext();
            var assert = scope.CreateContext();

            var customer = await arrange.SeedOneAsync(ShopData.Customer("Alice"), Ct);
            (await assert.Customers.SingleAsync(c => c.Id == customer.Id, Ct)).Name.ShouldBe("Alice");
        }

        await using var after = Fixture.CreateContext();
        (await after.Customers.CountAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_reset_inside_an_isolated_scope_is_rolled_back_with_it()
    {
        SkipIfUnavailable();
        await using (var seed = Fixture.CreateContext())
        {
            await ShopData.SeedEverythingAsync(seed);
        }

        await using (var scope = await Fixture.Database.BeginIsolatedScopeAsync(cancellationToken: Ct))
        {
            var context = scope.CreateContext();
            await context.Database.ResetDataAsync(cancellationToken: Ct);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        }

        await using var after = Fixture.CreateContext();
        (await ShopData.CountsAsync(after)).Values.ShouldAllBe(v => v > 0);
    }

    [Fact]
    public async Task Built_graphs_insert_on_the_engine()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();

        var lines = await context.CreateManyAsync<OrderLine>(20, cancellationToken: Ct);
        var customers = await context.CreateManyAsync<Customer>(20, fillOptional: true, cancellationToken: Ct);

        lines.ShouldAllBe(l => l.Id > 0 && l.Order.Customer.Id > 0);
        customers.ShouldAllBe(c => c.Id > 0);
        (await context.Customers.CountAsync(Ct)).ShouldBe(40);
        (await context.Customers.FirstAsync(Ct)).RowStamp.ShouldBe("generated");
    }

    [Fact]
    public async Task Sql_capture_sees_the_engine_commands()
    {
        SkipIfUnavailable();
        await using var context = Fixture.CreateContext();
        await context.CreateManyAsync<Order>(3, cancellationToken: Ct);
        using var sql = Fixture.Database.CaptureSql();

        // A projection: Oracle's MySQL provider cannot read a DateOnly column back, which is no
        // concern of the capture's.
        var rows = await context.Orders.Select(o => new { o.Id, o.Customer.Name }).ToListAsync(Ct);

        rows.Count.ShouldBe(3);
        sql.ShouldHaveExecuted(1).ShouldContain("JOIN");
    }
}

// One collection per engine, so each gets its own container and the engines run in parallel. The
// Engine trait is what lets CI shard the suite, or skip Docker entirely with a filter.

[CollectionDefinition("testing-postgres")]
public sealed class PostgreSqlCollectionDefinition : ICollectionFixture<PostgreSqlFixture>;

[CollectionDefinition("testing-sqlserver")]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>;

[CollectionDefinition("testing-mysql")]
public sealed class MySqlCollectionDefinition : ICollectionFixture<MySqlFixture>;

[Trait("Engine", "postgres17")]
[Collection("testing-postgres")]
public sealed class PostgreSqlEngineTests(PostgreSqlFixture fixture) : EngineTests(fixture)
{
    [Fact]
    public async Task A_refused_reset_inside_a_transaction_leaves_it_usable()
    {
        // PostgreSQL aborts the whole transaction on any error. Without a savepoint, a TRUNCATE refused
        // because of an unmapped referencing table cost the caller everything it had done before.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Unmapped\" (\"Id\" int PRIMARY KEY, \"CustomerId\" int NOT NULL REFERENCES \"Customers\" (\"Id\"))", Ct);
        try
        {
            var before = (await ShopData.CountsAsync(context))["Categories"];
            await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
            {
                context.Add(new Category { Name = "written before the reset" });
                await context.SaveChangesAsync(Ct);

                await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

                await transaction.CommitAsync(Ct);
            }

            (await ShopData.CountsAsync(context))["Categories"].ShouldBe(before + 1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"Unmapped\"", Ct);
        }
    }

    [Fact]
    public async Task A_catalog_check_that_fails_inside_a_transaction_leaves_it_usable()
    {
        // A cancelled or timed-out catalog query aborts the whole transaction on PostgreSQL. Shadowing
        // to_regclass for this transaction is a deterministic stand-in for that server-side error.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION public.to_regclass(text) RETURNS regclass LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'catalog check failed'; END $$",
            Ct);
        try
        {
            var before = (await ShopData.CountsAsync(context))["Categories"];
            await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
            {
                context.Add(new Category { Name = "written before the reset" });
                await context.SaveChangesAsync(Ct);
                await context.Database.ExecuteSqlRawAsync("SET LOCAL search_path = public, pg_catalog", Ct);

                (await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                    .Message.ShouldContain("catalog check failed");

                await transaction.CommitAsync(Ct);
            }

            (await ShopData.CountsAsync(context))["Categories"].ShouldBe(before + 1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS public.to_regclass(text)", Ct);
        }
    }

    [Fact]
    public async Task A_partition_named_in_ExcludeTable_is_refused_while_other_partitions_are_allowed()
    {
        // TRUNCATE Events empties every partition. That is the reset's job — unless one of them was
        // named in ExcludeTable, which a model that does not map the partition never saw.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Events" ("Id" int NOT NULL, "Year" int NOT NULL) PARTITION BY RANGE ("Year");
            CREATE TABLE "EventsArchive" PARTITION OF "Events" FOR VALUES FROM (2000) TO (2020);
            CREATE TABLE "EventsCurrent" PARTITION OF "Events" FOR VALUES FROM (2020) TO (2100);
            """,
            Ct);
        try
        {
            var events = new ResetTable(null, "Events");

            (await DescendantsAsync(context, ResetPlan.Create([events]))).ShouldBeEmpty();

            var excluded = await DescendantsAsync(context, ResetPlan.Create([events], excludedNames: [("EventsArchive", null)]));
            excluded.Select(d => d.Table).ShouldBe(["\"EventsArchive\""]);
            Should.Throw<InvalidOperationException>(() => ResetPlan.RefuseTruncatedDescendants(excluded));

            (await DescendantsAsync(context, ResetPlan.Create([events], excludedNames: [("EventsArchive", "public")]))).Count.ShouldBe(1);
            (await DescendantsAsync(context, ResetPlan.Create([events], excludedNames: [("EventsArchive", "other")]))).ShouldBeEmpty();
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"Events\"", Ct);
        }
    }

    private static async Task<List<DatabaseReference>> DescendantsAsync(DbContext context, ResetPlan plan)
    {
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await using DbCommand command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = DatabaseForeignKeys.PostgreSqlDescendants(plan);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<DatabaseReference>();
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(DatabaseForeignKeys.Read(reader));
            }

            return rows;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task An_unmapped_table_inheriting_from_a_reset_one_stops_the_reset()
    {
        // TRUNCATE without ONLY empties every descendant: the archive would go with Customers.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE \"CustomersArchive\" () INHERITS (\"Customers\")", Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"CustomersArchive\" SELECT * FROM ONLY \"Customers\" LIMIT 1", Ct);
            var before = await ShopData.CountsAsync(context);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("'\"CustomersArchive\"' does but is not part of the reset");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM \"CustomersArchive\"")).ShouldBe(1);
            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"CustomersArchive\"", Ct);
        }
    }

    [Fact]
    public async Task A_table_outside_the_model_that_references_a_reset_one_stops_the_reset()
    {
        // Without CASCADE the TRUNCATE refuses, rather than emptying a table nobody asked it to touch.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"UnmappedAudit\" (\"Id\" int PRIMARY KEY, \"CustomerId\" int NOT NULL REFERENCES \"Customers\" (\"Id\"))",
            Ct);
        try
        {
            var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
            await context.Database.ExecuteSqlAsync($"INSERT INTO \"UnmappedAudit\" VALUES (1, {customer})", Ct);

            await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

            (await ScalarAsync(context, "SELECT COUNT(*) FROM \"UnmappedAudit\"")).ShouldBe(1);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE \"UnmappedAudit\"", Ct);
        }
    }
}

[Trait("Engine", "sqlserver")]
[Collection("testing-sqlserver")]
public sealed class SqlServerEngineTests(SqlServerFixture fixture) : EngineTests(fixture)
{
    [Fact]
    public Task A_table_outside_the_model_that_references_a_reset_one_stops_the_reset()
        // Its constraint stays enforced, but the reset deletes table by table: without the catalog check
        // it failed only after emptying every table ahead of Customers.
        => AssertUnmappedReferenceStopsResetAsync<InvalidOperationException>(
            "CREATE TABLE Unmapped (Id int PRIMARY KEY, CustomerId int NOT NULL REFERENCES Customers (Id))",
            "DROP TABLE Unmapped");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_reset_that_fails_part_way_changes_nothing(bool inTransaction)
        => AssertPartialFailureChangesNothingAsync(
            "CREATE TRIGGER KeepCustomers ON Customers INSTEAD OF DELETE AS THROW 50000, 'kept', 1;",
            "DROP TRIGGER IF EXISTS KeepCustomers",
            inTransaction);

    [Fact]
    public async Task A_re_check_that_fails_rolls_back_the_deletes_and_the_suspended_constraints()
    {
        // A trigger puts back a category whose parent does not exist, which NOCHECK lets through: the
        // re-check of Categories then fails. Run after the commit, that left every table emptied and
        // the constraints off.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER LeaveOrphan ON Categories AFTER DELETE AS INSERT INTO Categories (Name, ParentId) VALUES (N'orphan', 2147483647);",
            Ct);
        try
        {
            await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(cancellationToken: Ct));

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS LeaveOrphan", Ct);
        }
    }

    [Fact]
    public async Task A_table_under_an_enabled_row_level_security_filter_is_refused()
    {
        // A filter predicate applies to DELETE too: a reset under one tenant's session context removed
        // that tenant's rows, reported success, and left every other tenant's.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION dbo.NoTags(@code nvarchar(450)) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS allowed WHERE 1 = 0;", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE SECURITY POLICY TagFilter ADD FILTER PREDICATE dbo.NoTags(Code) ON dbo.Tags WITH (STATE = ON);", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("the security policy 'TagFilter' filters it");

            await context.Database.ExecuteSqlRawAsync("ALTER SECURITY POLICY TagFilter WITH (STATE = OFF);", Ct);
            (await ShopData.CountsAsync(context)).ShouldBe(before);

            // With the policy off, the reset sees every row and runs.
            await context.Database.ResetDataAsync(cancellationToken: Ct);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP SECURITY POLICY IF EXISTS TagFilter;", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS dbo.NoTags;", Ct);
        }
    }

    [Fact]
    public async Task A_login_that_cannot_see_security_policies_is_refused_rather_than_trusted()
    {
        // Catalog views show only what the login may see: without VIEW DEFINITION the policy check above
        // would find nothing, and a filtered table would be half-reset with success reported.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE USER limited_user WITHOUT LOGIN; GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON SCHEMA::dbo TO limited_user;", Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = 'limited_user'", Ct);
            try
            {
                (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                    .Message.ShouldContain("cannot see the database's row-level security policies");
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync("REVERT", Ct);
            }

            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS limited_user;", Ct);
        }
    }

    [Fact]
    public async Task Under_a_non_dbo_default_schema_the_check_follows_the_table_the_delete_resolves_to()
    {
        // For a user whose default schema is app, unqualified Customers is app.Customers. Checking
        // dbo.Customers instead missed app.Unmapped, and its ON DELETE CASCADE emptied it.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE SCHEMA app", Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE app.Customers (Id int PRIMARY KEY);
                CREATE TABLE app.Unmapped (Id int PRIMARY KEY, CustomerId int NOT NULL REFERENCES app.Customers (Id) ON DELETE CASCADE);
                INSERT INTO app.Customers VALUES (1);
                INSERT INTO app.Unmapped VALUES (1, 1);
                CREATE USER reset_user WITHOUT LOGIN WITH DEFAULT_SCHEMA = app;
                GRANT CONTROL ON SCHEMA::app TO reset_user;
                GRANT CONTROL ON SCHEMA::dbo TO reset_user;
                GRANT VIEW DEFINITION TO reset_user;
                """,
                Ct);

            await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER = 'reset_user'", Ct);
            try
            {
                (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                    .Message.ShouldContain("'Unmapped', which the reset does not empty");
            }
            finally
            {
                await context.Database.ExecuteSqlRawAsync("REVERT", Ct);
            }

            (await ScalarAsync(context, "SELECT COUNT(*) FROM app.Unmapped")).ShouldBe(1);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM app.Customers")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF USER_ID('reset_user') IS NOT NULL DROP USER reset_user;
                DROP TABLE IF EXISTS app.Unmapped;
                DROP TABLE IF EXISTS app.Customers;
                """,
                Ct);
            await context.Database.ExecuteSqlRawAsync("DROP SCHEMA app", Ct);
        }
    }

    /// <summary>The cyclic tables' constraints are back on, and trusted — re-checked, not merely re-enabled.</summary>
    protected override async Task AssertConstraintsRestoredAsync(ShopContext context)
        => (await ScalarAsync(context, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_disabled = 1 OR is_not_trusted = 1"))
            .ShouldBe(0);
}

[Trait("Engine", "mysql")]
[Collection("testing-mysql")]
public sealed class MySqlEngineTests(MySqlFixture fixture) : EngineTests(fixture)
{
    [Fact]
    public async Task A_table_differing_only_in_case_is_not_taken_for_a_reset_one()
    {
        // MySQL on Linux keeps table names case-sensitive by default: orders is not Orders.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        if (await ScalarAsync(context, "SELECT @@lower_case_table_names") != 0)
        {
            Assert.Skip("This server folds table names to one case.");
        }

        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE orders (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id))", Ct);
        try
        {
            var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO orders (Id, CustomerId) VALUES (1, {0})", [customer], Ct);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("'orders', which the reset does not empty");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM orders")).ShouldBe(1);
            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v > 0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS orders", Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_reset_that_fails_part_way_changes_nothing(bool inTransaction)
        // Deleting rather than truncating: TRUNCATE fires no trigger, and commits on its own anyway.
        => AssertPartialFailureChangesNothingAsync(
            "CREATE TRIGGER KeepCustomers BEFORE DELETE ON Customers FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'kept'",
            "DROP TRIGGER IF EXISTS KeepCustomers",
            inTransaction,
            o => o.RestartIdentity = false);

    [Fact]
    public Task A_table_outside_the_model_that_references_a_reset_one_stops_the_reset()
        // FOREIGN_KEY_CHECKS = 0 would let the truncate through, so the catalog check refuses first.
        => AssertUnmappedReferenceStopsResetAsync<InvalidOperationException>(
            "CREATE TABLE Unmapped (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id))",
            "DROP TABLE Unmapped");

    protected override async Task AssertConstraintsRestoredAsync(ShopContext context)
        => (await ScalarAsync(context, "SELECT @@FOREIGN_KEY_CHECKS")).ShouldBe(1);
}
