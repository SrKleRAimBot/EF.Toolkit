using System.Data.Common;
using System.Globalization;
using EFToolkit.Testing.Integration.Infrastructure;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    private sealed class Reading
    {
        public int Id { get; set; }
        public decimal Fraction { get; set; }
        public decimal Declared { get; set; }
        public string Code { get; set; } = "";
        public DateTime[] Moments { get; set; } = [];
        public DateTime TakenAt { get; set; }
        public DateTime LocalTime { get; set; }
    }

    private sealed class ReadingContext(DbContextOptions<ReadingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Reading>(b =>
            {
                b.ToTable("Readings");
                b.Property(r => r.Fraction).HasPrecision(18, 18);

                // Precision only in the column type: no precision facet, just the resolved mapping.
                b.Property(r => r.Declared).HasColumnType("numeric(2,2)");
                b.Property(r => r.Code).HasColumnType("varchar(4)");
                b.Property(r => r.Moments).HasColumnType("timestamp without time zone[]");
                b.Property(r => r.TakenAt).HasColumnType("timestamptz(3)");
                b.Property(r => r.LocalTime).HasColumnType("timestamp(3) without time zone");
            });
    }

    [Fact]
    public async Task Generated_values_fit_wide_fractions_and_timestamp_columns_with_a_precision()
    {
        // numeric(18,18) got integers it overflows on, and timestamptz(3) an unspecified DateTime, which
        // Npgsql refuses for a timestamp with time zone.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new ReadingContext(
            new DbContextOptionsBuilder<ReadingContext>().UseNpgsql(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Readings\" (\"Id\" serial PRIMARY KEY, \"Fraction\" numeric(18,18) NOT NULL, \"Declared\" numeric(2,2) NOT NULL, \"Code\" varchar(4) NOT NULL, \"Moments\" timestamp without time zone[] NOT NULL, "
                + "\"TakenAt\" timestamptz(3) NOT NULL, \"LocalTime\" timestamp(3) without time zone NOT NULL)",
            Ct);
        try
        {
            var readings = await context.CreateManyAsync<Reading>(3, cancellationToken: Ct);

            readings.Select(r => r.Fraction).Distinct().Count().ShouldBe(3);
            readings.ShouldAllBe(r => r.Declared > -1m && r.Declared < 1m);
            (await context.Set<Reading>().CountAsync(Ct)).ShouldBe(3);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Readings\"", Ct);
        }
    }

    private sealed class Rounding
    {
        public int Id { get; set; }
        public decimal Thousands { get; set; }
        public decimal Trillions { get; set; }
    }

    private sealed class RoundingContext(DbContextOptions<RoundingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Rounding>(b =>
            {
                b.ToTable("Roundings");
                b.Property(r => r.Thousands).HasColumnType("numeric(2,-3)");
                b.HasIndex(r => r.Thousands).IsUnique();
                b.Property(r => r.Trillions).HasColumnType("numeric(20,-12)");
                b.HasIndex(r => r.Trillions).IsUnique();
            });
    }

    [Fact]
    public async Task Generated_decimals_stay_distinct_in_a_column_with_a_negative_scale()
    {
        // numeric(2,-3) rounds to thousands: 1 and 2 were both stored as 0, and the unique index refused
        // the second row. numeric(20,-12) too, once the step was cut short to fit 20 digits in a decimal.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new RoundingContext(
            new DbContextOptionsBuilder<RoundingContext>().UseNpgsql(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Roundings\" (\"Id\" serial PRIMARY KEY, \"Thousands\" numeric(2,-3) NOT NULL UNIQUE, \"Trillions\" numeric(20,-12) NOT NULL UNIQUE)", Ct);
        try
        {
            await context.CreateManyAsync<Rounding>(3, cancellationToken: Ct);

            await using var fresh = new RoundingContext(
                new DbContextOptionsBuilder<RoundingContext>().UseNpgsql(Fixture.ConnectionString).Options);
            var stored = await fresh.Set<Rounding>().ToListAsync(Ct);
            stored.Select(r => r.Thousands).Distinct().Count().ShouldBe(3);
            stored.Select(r => r.Trillions).Distinct().Count().ShouldBe(3);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Roundings\"", Ct);
        }
    }

    private sealed class DottedFirst
    {
        public int Id { get; set; }
        public decimal Rate { get; set; }
    }

    private sealed class DottedSecond
    {
        public int Id { get; set; }
        public decimal Rate { get; set; }
    }

    /// <summary>"a.b"."c" and "a"."b.c": two tables whose schema-qualified names read the same.</summary>
    private sealed class DottedContext(DbContextOptions<DottedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DottedFirst>(b => { b.ToTable("c", "a.b"); b.Property(d => d.Rate).HasPrecision(2, 2); });
            modelBuilder.Entity<DottedSecond>(b => { b.ToTable("b.c", "a"); b.Property(d => d.Rate).HasPrecision(2, 2); });
        }
    }

    [Fact]
    public async Task Tables_whose_qualified_names_read_alike_count_their_bounded_columns_apart()
    {
        // Both were counted under "a.b.c": one row in the first, 99 in the second, and the next in the
        // first repeated the first row's rate.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new DottedContext(
            new DbContextOptionsBuilder<DottedContext>().UseNpgsql(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE SCHEMA \"a.b\"; CREATE SCHEMA \"a\"; "
                + "CREATE TABLE \"a.b\".\"c\" (\"Id\" serial PRIMARY KEY, \"Rate\" numeric(2,2) NOT NULL UNIQUE); "
                + "CREATE TABLE \"a\".\"b.c\" (\"Id\" serial PRIMARY KEY, \"Rate\" numeric(2,2) NOT NULL UNIQUE)",
            Ct);
        try
        {
            await context.CreateAsync<DottedFirst>(cancellationToken: Ct);
            await context.CreateManyAsync<DottedSecond>(99, cancellationToken: Ct);
            await context.CreateAsync<DottedFirst>(cancellationToken: Ct);

            (await context.Set<DottedFirst>().CountAsync(Ct)).ShouldBe(2);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS \"a.b\" CASCADE; DROP SCHEMA IF EXISTS \"a\" CASCADE", Ct);
        }
    }

    [Fact]
    public async Task A_truncate_trigger_writing_into_another_reset_table_fails_the_reset()
    {
        // The trigger is on Customers but writes into Tags, which was truncated in the same statement:
        // only the trigger's own table was counted, and the reset committed with Tags holding a row.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION audit_tags() RETURNS trigger LANGUAGE plpgsql AS "
                + "$$ BEGIN INSERT INTO \"Tags\" (\"Code\", \"Label\") VALUES ('audit', 'written by a trigger'); RETURN NULL; END $$; "
                + "CREATE TRIGGER audit_tags AFTER TRUNCATE ON \"Customers\" FOR EACH STATEMENT EXECUTE FUNCTION audit_tags();",
            Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("Tags'");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS audit_tags ON \"Customers\"; DROP FUNCTION IF EXISTS audit_tags();", Ct);
        }
    }

    private sealed class Happening
    {
        public int Id { get; set; }

        public int Year { get; set; }
    }

    private sealed class HappeningContext(DbContextOptions<HappeningContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Happening>(b =>
            {
                b.ToTable("Happenings");
                b.HasKey(h => new { h.Id, h.Year });
            });
    }

    [Fact]
    public async Task A_truncate_trigger_on_a_partition_only_the_parent_maps_fails_the_reset()
    {
        // TRUNCATE Happenings fires the partition's trigger too, but the search looked at mapped tables only.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new HappeningContext(
            new DbContextOptionsBuilder<HappeningContext>().UseNpgsql(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE "Happenings" ("Id" int NOT NULL, "Year" int NOT NULL) PARTITION BY RANGE ("Year");
            CREATE TABLE "HappeningsOld" PARTITION OF "Happenings" FOR VALUES FROM (2000) TO (2020);
            CREATE TABLE "HappeningsNew" PARTITION OF "Happenings" FOR VALUES FROM (2020) TO (2100);
            INSERT INTO "Happenings" VALUES (1, 2010), (2, 2025);
            CREATE FUNCTION refill_happenings() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN INSERT INTO "Happenings" VALUES (99, 2011); RETURN NULL; END $$;
            CREATE TRIGGER refill_happenings AFTER TRUNCATE ON "HappeningsOld" FOR EACH STATEMENT EXECUTE FUNCTION refill_happenings();
            """,
            Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("Happenings'");

            (await context.Set<Happening>().Select(h => h.Id).OrderBy(id => id).ToListAsync(Ct)).ShouldBe([1, 2]);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                "DROP TABLE IF EXISTS \"Happenings\"; DROP FUNCTION IF EXISTS refill_happenings();", Ct);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_truncate_trigger_that_puts_rows_back_fails_the_reset_and_rolls_it_back(bool inTransaction)
    {
        // A statement-level AFTER TRUNCATE trigger inserted a row straight back; TRUNCATE succeeded and the
        // reset reported success with it there.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION refill_tags() RETURNS trigger LANGUAGE plpgsql AS "
                + "$$ BEGIN INSERT INTO \"Tags\" (\"Code\", \"Label\") VALUES ('back', 'put back'); RETURN NULL; END $$; "
                + "CREATE TRIGGER refill_tags AFTER TRUNCATE ON \"Tags\" FOR EACH STATEMENT EXECUTE FUNCTION refill_tags();",
            Ct);
        try
        {
            await using (var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null)
            {
                (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                    .Message.ShouldContain("Tags'");
                if (transaction is not null)
                {
                    await transaction.CommitAsync(Ct);
                }
            }

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await context.Tags.CountAsync(t => t.Code == "back", Ct)).ShouldBe(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS refill_tags ON \"Tags\"; DROP FUNCTION IF EXISTS refill_tags();", Ct);
        }
    }

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
        // A cancelled or timed-out catalog query aborts the whole transaction on PostgreSQL. An operator
        // the catalog query resolves through the search_path — <> on regclass, raising — is a
        // deterministic stand-in for that server-side error (its functions are named with pg_catalog). Cheap
        // enough that the planner evaluates it before the condition it is ANDed with.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE FUNCTION public.fail_ne(regclass, regclass) RETURNS boolean LANGUAGE plpgsql COST 0.001 AS $$ BEGIN RAISE EXCEPTION 'catalog check failed'; END $$; "
                + "CREATE OPERATOR public.<> (LEFTARG = regclass, RIGHTARG = regclass, FUNCTION = public.fail_ne)",
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
            await context.Database.ExecuteSqlRawAsync(
                "DROP OPERATOR IF EXISTS public.<> (regclass, regclass); DROP FUNCTION IF EXISTS public.fail_ne(regclass, regclass)", Ct);
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
    public async Task A_function_shadowing_to_regclass_cannot_hide_an_inheriting_table()
    {
        // With search_path app, pg_catalog, public and an app.to_regclass returning NULL, the unqualified
        // lookups found no reset table at all: the archive went unseen, and TRUNCATE emptied it.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE SCHEMA IF NOT EXISTS app; "
                + "CREATE FUNCTION app.to_regclass(text) RETURNS pg_catalog.regclass LANGUAGE sql AS 'SELECT NULL::pg_catalog.regclass'; "
                + "CREATE TABLE \"CustomersArchive\" () INHERITS (\"Customers\");",
            Ct);
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"CustomersArchive\" SELECT * FROM ONLY \"Customers\" LIMIT 1", Ct);
            await context.Database.ExecuteSqlRawAsync("SET search_path = app, pg_catalog, public", Ct);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("CustomersArchive");

            await context.Database.ExecuteSqlRawAsync("SET search_path = public", Ct);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM \"CustomersArchive\"")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                "SET search_path = public; DROP TABLE IF EXISTS \"CustomersArchive\"; DROP SCHEMA IF EXISTS app CASCADE", Ct);
            await context.Database.CloseConnectionAsync();
        }
    }

    private sealed class LocalSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class PublicSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Two mappings of one table: one leaves the schema to the search_path, one writes it out.</summary>
    private sealed class SettingsContext(DbContextOptions<SettingsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LocalSetting>().ToTable("Settings");
            modelBuilder.Entity<PublicSetting>().ToTable("Settings", "public");
        }
    }

    [Fact]
    public async Task A_table_kept_under_one_mapping_is_not_emptied_under_another()
    {
        // ExcludeEntity kept (null, Settings), but the mapping naming public.Settings was still reset:
        // compared by the model's names they were two tables, and TRUNCATE emptied the one kept.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new SettingsContext(
            new DbContextOptionsBuilder<SettingsContext>().UseNpgsql(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE public.\"Settings\" (\"Id\" int PRIMARY KEY, \"Name\" text NOT NULL); "
                + "INSERT INTO public.\"Settings\" VALUES (1, 'keep me')",
            Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.ExcludeEntity<LocalSetting>(), Ct)))
                .Message.ShouldContain("Settings");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM public.\"Settings\"")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS public.\"Settings\"", Ct);
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

    private sealed class BigSeed
    {
        public decimal Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class BigSeedContext(DbContextOptions<BigSeedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<BigSeed>(b =>
            {
                b.ToTable("BigSeeds");
                b.Property(x => x.Id).HasColumnType("decimal(20, 0)").ValueGeneratedOnAdd();
            });
    }

    [Fact]
    public async Task A_failed_reset_in_an_explicitly_enlisted_transaction_keeps_the_caller_s_work()
    {
        // A transaction enlisted through EF's connection shows in neither CurrentTransaction nor
        // Transaction.Current: the reset opened one of its own, and its ROLLBACK took the caller's insert.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER SoftDeleteCustomers ON Customers INSTEAD OF DELETE AS "
                + "UPDATE Customers SET IsActive = 0 WHERE Id IN (SELECT Id FROM deleted);",
            Ct);
        try
        {
            using var transaction = new System.Transactions.CommittableTransaction();
            await context.Database.OpenConnectionAsync(Ct);
            try
            {
                ((RelationalConnection)context.GetService<IRelationalConnection>()).EnlistTransaction(transaction);
                await context.Database.ExecuteSqlRawAsync("INSERT INTO Tags (Code, Label) VALUES (N'mine', N'the caller''s')", Ct);

                await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(o => o.ExcludeEntity<Tag>(), Ct));

                (await ScalarAsync(context, "SELECT COUNT(*) FROM Tags WHERE Code = N'mine'")).ShouldBe(1);
                transaction.Rollback();
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS SoftDeleteCustomers", Ct);
        }
    }

    [Fact]
    public async Task A_soft_deleting_trigger_that_keeps_the_rows_fails_the_reset_and_rolls_it_back()
    {
        // The trigger marks customers inactive instead of deleting them and returns normally: the reset
        // reported success with every customer, and every unique email, still there.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER SoftDeleteCustomers ON Customers INSTEAD OF DELETE AS "
                + "UPDATE Customers SET IsActive = 0 WHERE Id IN (SELECT Id FROM deleted);",
            Ct);
        try
        {
            (await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("The reset left rows in 'dbo.Customers'");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS SoftDeleteCustomers", Ct);
        }
    }

    private sealed class Holiday
    {
        public int Id { get; set; }
        public DateTime Day { get; set; }
    }

    private sealed class HolidayContext(DbContextOptions<HolidayContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Holiday>(b =>
            {
                b.ToTable("Holidays");
                b.Property(h => h.Day).HasColumnType("date");
                b.HasIndex(h => h.Day).IsUnique();
            });
    }

    private sealed class LocalSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class DboSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>Two mappings of one table: one leaves the schema to the default, one writes dbo out.</summary>
    private sealed class SettingsContext(DbContextOptions<SettingsContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LocalSetting>().ToTable("Settings");
            modelBuilder.Entity<DboSetting>().ToTable("Settings", "dbo");
        }
    }

    [Fact]
    public async Task A_table_kept_under_one_mapping_is_not_emptied_under_another()
    {
        // ExcludeEntity kept Settings, but the mapping naming dbo.Settings was still deleted from: by the
        // model's names they were two tables, to SQL Server one.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new SettingsContext(
            new DbContextOptionsBuilder<SettingsContext>().UseSqlServer(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE dbo.Settings (Id int PRIMARY KEY, Name nvarchar(50) NOT NULL); INSERT INTO dbo.Settings VALUES (1, N'keep me')",
            Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.ExcludeEntity<LocalSetting>(), Ct)))
                .Message.ShouldContain("Settings");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM dbo.Settings")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS dbo.Settings", Ct);
        }
    }

    [Fact]
    public async Task Generated_DateTimes_in_a_date_column_are_distinct_days()
    {
        // A minute apart, two generated values were one date: the unique index refused the second row.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new HolidayContext(
            new DbContextOptionsBuilder<HolidayContext>().UseSqlServer(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Holidays (Id int IDENTITY PRIMARY KEY, Day date NOT NULL UNIQUE)", Ct);
        try
        {
            var holidays = await context.CreateManyAsync<Holiday>(3, cancellationToken: Ct);

            holidays.Select(h => h.Day.Date).Distinct().Count().ShouldBe(3);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Holidays", Ct);
        }
    }

    [Fact]
    public async Task A_decimal_identity_seeded_past_the_bigint_range_restarts_at_its_seed()
    {
        // The reseed arithmetic was bigint: assigning this seed overflowed and the reset failed.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new BigSeedContext(
            new DbContextOptionsBuilder<BigSeedContext>().UseSqlServer(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE BigSeeds (Id decimal(20, 0) IDENTITY(10000000000000000000, 1) PRIMARY KEY, Name nvarchar(10) NOT NULL)", Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync("INSERT INTO BigSeeds (Name) VALUES (N'a'), (N'b')", Ct);

            await context.Database.ResetDataAsync(cancellationToken: Ct);

            context.Add(new BigSeed { Name = "c" });
            await context.SaveChangesAsync(Ct);
            (await context.Set<BigSeed>().SingleAsync(Ct)).Id.ShouldBe(10000000000000000000m);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS BigSeeds", Ct);
        }
    }

    [Fact]
    public async Task A_view_under_a_mapped_table_s_name_is_refused_and_its_base_table_keeps_its_rows()
    {
        // DELETE through an updatable view deletes from its base table.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var tags = await context.Tags.CountAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("EXEC sp_rename 'dbo.Tags', 'TagsBase'", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE VIEW dbo.Tags AS SELECT * FROM dbo.TagsBase", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("in the database it is a view");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM dbo.TagsBase")).ShouldBe(tags);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP VIEW IF EXISTS dbo.Tags", Ct);
            await context.Database.ExecuteSqlRawAsync("EXEC sp_rename 'dbo.TagsBase', 'Tags'", Ct);
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
    private sealed class KeptSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    private sealed class NamedSetting
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    /// <summary>One table mapped twice: once by its bare name, once with the database written out.</summary>
    private sealed class QualifiedSettingsContext(DbContextOptions<QualifiedSettingsContext> options, string database) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KeptSetting>().ToTable("Settings");
            modelBuilder.Entity<NamedSetting>().ToTable("Settings", database);
        }
    }

    private sealed class ArchivedOrder
    {
        public int Id { get; set; }
    }

    /// <summary>A table mapped into another database than the connection's.</summary>
    private sealed class ArchiveContext(DbContextOptions<ArchiveContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<ArchivedOrder>().ToTable("ArchivedOrders", "reset_archive");
    }

    [Fact]
    public async Task A_table_in_another_database_is_refused_before_anything_is_deleted()
    {
        // The checks before a MySQL reset read the connection's database only: a cascade into a kept table
        // there, or a MyISAM table there, went unseen while DELETE FROM reset_archive.ArchivedOrders ran.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new ArchiveContext(
            new DbContextOptionsBuilder<ArchiveContext>().UseMySQL(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE DATABASE IF NOT EXISTS reset_archive; "
                + "CREATE TABLE reset_archive.ArchivedOrders (Id int PRIMARY KEY); "
                + "INSERT INTO reset_archive.ArchivedOrders VALUES (1)",
            Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("another database");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM reset_archive.ArchivedOrders")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS reset_archive", Ct);
        }
    }

    [Fact]
    public async Task A_table_kept_under_one_mapping_is_not_emptied_under_another()
    {
        // ExcludeEntity kept Settings, but the mapping naming the database was still deleted from: two
        // tables to the model, one to MySQL, whose unqualified names are the current database's.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        var database = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(Fixture.ConnectionString).Database;
        await using var context = new QualifiedSettingsContext(
            new DbContextOptionsBuilder<QualifiedSettingsContext>().UseMySQL(Fixture.ConnectionString).Options, database);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Settings (Id int PRIMARY KEY, Name varchar(50) NOT NULL); INSERT INTO Settings VALUES (1, 'keep me')", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.ExcludeEntity<KeptSetting>(), Ct)))
                .Message.ShouldContain("Settings");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM Settings")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS Settings", Ct);
        }
    }

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

    // A trigger on Products writes an order line for an order that does not exist, into OrderLines,
    // which the reset has already emptied. MySQL never re-checks a row written while checks were off,
    // so with them suspended that orphan used to be committed.
    private const string LeaveOrphan =
        "CREATE TRIGGER LeaveOrphan AFTER DELETE ON Products FOR EACH ROW "
        + "INSERT INTO OrderLines (OrderId, ProductId, Quantity) VALUES (2147483647, OLD.Id, 1)";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_trigger_that_orphans_a_row_while_checks_are_suspended_fails_the_reset_and_rolls_it_back(bool inTransaction)
    {
        // Categories, Teams and Players are on cycles, so the deletes run with checks suspended.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(LeaveOrphan, Ct);
        try
        {
            await using (var transaction = inTransaction ? await context.Database.BeginTransactionAsync(Ct) : null)
            {
                (await Should.ThrowAsync<InvalidOperationException>(
                        () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                    .Message.ShouldContain("violate a foreign key");
                if (transaction is not null)
                {
                    await transaction.CommitAsync(Ct);
                }
            }

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM OrderLines WHERE OrderId = 2147483647")).ShouldBe(0);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS LeaveOrphan", Ct);
        }
    }

    private static async Task CreateOutsideTablesAsync(ShopContext context)
    {
        // Neither table is in the model, and neither references a reset table: the inbound preflight
        // has nothing to refuse.
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE OutParent (Id int PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE OutChild (Id int PRIMARY KEY, ParentId int NOT NULL, FOREIGN KEY (ParentId) REFERENCES OutParent (Id))", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO OutParent VALUES (1)", Ct);
    }

    private static async Task DropOutsideTablesAsync(ShopContext context)
    {
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS WriteOutside", Ct);
        await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS OutChild", Ct);
        await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS OutParent", Ct);
    }

    [Fact]
    public async Task A_trigger_that_orphans_a_row_in_a_table_outside_the_reset_fails_the_reset_too()
    {
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await CreateOutsideTablesAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER WriteOutside AFTER DELETE ON Categories FOR EACH ROW INSERT INTO OutChild VALUES (OLD.Id + 1000, 2147483647)", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain("violate a foreign key");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM OutChild")).ShouldBe(0);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await DropOutsideTablesAsync(context);
        }
    }

    [Fact]
    public async Task A_trigger_the_resetting_user_cannot_see_is_covered_all_the_same()
    {
        // information_schema.TRIGGERS shows a trigger only to a user with TRIGGER on its table, but the
        // trigger fires for everyone, as its definer: asking whether there is one let this orphan through.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await CreateOutsideTablesAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER WriteOutside AFTER DELETE ON Categories FOR EACH ROW INSERT INTO OutChild VALUES (OLD.Id + 1000, 2147483647)", Ct);
        var builder = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(Fixture.ConnectionString);
        await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'limited'@'%'", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE USER 'limited'@'%' IDENTIFIED BY 'limited'", Ct);
        var grant = "GRANT SELECT, INSERT, UPDATE, DELETE ON `" + builder.Database + "`.* TO 'limited'@'%'";
        await context.Database.ExecuteSqlRawAsync(grant, Ct);
        builder.UserID = "limited";
        builder.Password = "limited";
        try
        {
            await using var limited = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseMySQL(builder.ConnectionString).Options);
            (await ScalarAsync(limited, "SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_NAME = 'WriteOutside'")).ShouldBe(0);

            (await Should.ThrowAsync<InvalidOperationException>(
                    () => limited.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain("violate a foreign key");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM OutChild")).ShouldBe(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'limited'@'%'", Ct);
            await DropOutsideTablesAsync(context);
        }
    }

    private ShopContext LimitedContext(string user)
    {
        var builder = new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(Fixture.ConnectionString) { UserID = user, Password = user };
        return new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseMySQL(builder.ConnectionString).Options);
    }

    private string DatabaseName => new MySql.Data.MySqlClient.MySqlConnectionStringBuilder(Fixture.ConnectionString).Database;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_table_the_login_cannot_see_keeps_its_rows_and_its_parents(bool acyclic)
    {
        // Granted only the mapped tables, the login sees neither HiddenChild nor its foreign key. With the
        // checks off a reset emptied Customers under it; with them on, an ON DELETE CASCADE emptied
        // HiddenChild itself. Such a login is refused.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE HiddenChild (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO HiddenChild VALUES (1, {0})", [customer], Ct);
        var tables = await context.Database.SqlQueryRaw<string>(
                "SELECT TABLE_NAME AS Value FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'HiddenChild'")
            .ToListAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'tables_only'@'%'", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE USER 'tables_only'@'%' IDENTIFIED BY 'tables_only'", Ct);
        foreach (var table in tables)
        {
            var grant = "GRANT SELECT, INSERT, UPDATE, DELETE, ALTER, DROP ON `" + DatabaseName + "`.`" + table + "` TO 'tables_only'@'%'";
            await context.Database.ExecuteSqlRawAsync(grant, Ct);
        }

        try
        {
            await using var limited = LimitedContext("tables_only");
            (await ScalarAsync(limited, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_NAME = 'HiddenChild'")).ShouldBe(0);

            (await Should.ThrowAsync<InvalidOperationException>(() => acyclic
                    ? limited.Database.ResetDataAsync(o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>(), Ct)
                    : limited.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("holds no privilege on the whole database");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM HiddenChild")).ShouldBe(1);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'tables_only'@'%'", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS HiddenChild", Ct);
        }
    }

    [Fact]
    public async Task A_trigger_that_orphans_a_row_in_another_database_fails_the_reset()
    {
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync("CREATE DATABASE IF NOT EXISTS eftesting_other", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE eftesting_other.OutParent (Id int PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE eftesting_other.OutChild (Id int PRIMARY KEY, ParentId int NOT NULL, FOREIGN KEY (ParentId) REFERENCES eftesting_other.OutParent (Id))", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER WriteOutside AFTER DELETE ON Categories FOR EACH ROW INSERT INTO eftesting_other.OutChild VALUES (OLD.Id + 1000, 2147483647)", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain("violate a foreign key");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM eftesting_other.OutChild")).ShouldBe(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS WriteOutside", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP DATABASE IF EXISTS eftesting_other", Ct);
        }
    }

    [Fact]
    public async Task A_temporary_table_shadowing_a_mapped_one_is_refused_and_both_keep_their_rows()
    {
        // MySQL resolves Customers to the session's temporary table first, even qualified: the reset
        // emptied that, reported success, and left every real customer.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TEMPORARY TABLE Customers (Id int PRIMARY KEY)", Ct);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO Customers VALUES (1)", Ct);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("temporary table of that name");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM Customers")).ShouldBe(1);
            await context.Database.ExecuteSqlRawAsync("DROP TEMPORARY TABLE Customers", Ct);
            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            // The connection goes back to the pool with its session, temporary tables included.
            await context.Database.ExecuteSqlRawAsync("DROP TEMPORARY TABLE IF EXISTS Customers", Ct);
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task A_trigger_writing_into_a_table_already_emptied_fails_the_reset()
    {
        // An audit trigger on a table deleted later wrote into one the reset had already emptied, and the
        // reset reported success with the row in it.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        var order = DatabaseResetExtensions.GenerateResetScript(context.Database, o => o.RestartIdentity = false).Body
            .Select(b => b["DELETE FROM `".Length..b.IndexOf('`', "DELETE FROM `".Length)])
            .ToList();
        var later = order[order.IndexOf("Tags") + 1];
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        var trigger = "CREATE TRIGGER AuditInto AFTER DELETE ON `" + later + "` FOR EACH ROW "
            + "INSERT IGNORE INTO Tags (Code, Label) VALUES ('audit', 'written during the reset')";
        await context.Database.ExecuteSqlRawAsync(trigger, Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain("The reset left rows in 'Tags'");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS AuditInto", Ct);
        }
    }

    [Fact]
    public async Task A_table_without_transactions_is_refused_before_anything_is_deleted()
    {
        // A MyISAM table ignores the rollback: a reset failing after it was emptied left it emptied.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        // MyISAM keys are limited to 1000 bytes, so the key column is narrowed for the test and put back after.
        var codeType = await context.Database.SqlQueryRaw<string>(
                "SELECT COLUMN_TYPE AS Value FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Tags' AND COLUMN_NAME = 'Code'")
            .SingleAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("ALTER TABLE Tags MODIFY Code varchar(100) NOT NULL, ENGINE = MyISAM", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("'Tags': it uses the MyISAM storage engine");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
        }
        finally
        {
            var restore = "ALTER TABLE Tags ENGINE = InnoDB, MODIFY Code " + codeType + " NOT NULL";
            await context.Database.ExecuteSqlRawAsync(restore, Ct);
        }
    }

    [Fact]
    public async Task A_temporary_table_shadowing_one_the_check_reads_is_refused_before_anything_is_deleted()
    {
        // An empty temporary OutChild answered for the permanent one: the trigger's orphan went unseen.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await CreateOutsideTablesAsync(context);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER WriteOutside AFTER DELETE ON Categories FOR EACH ROW INSERT INTO OutChild VALUES (OLD.Id + 1000, 2147483647)", Ct);
        await context.Database.OpenConnectionAsync(Ct);
        try
        {
            await context.Database.ExecuteSqlRawAsync("CREATE TEMPORARY TABLE OutChild (Id int PRIMARY KEY, ParentId int NOT NULL)", Ct);

            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain("temporary table of that name");

            await context.Database.ExecuteSqlRawAsync("DROP TEMPORARY TABLE OutChild", Ct);
            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM OutChild")).ShouldBe(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TEMPORARY TABLE IF EXISTS OutChild", Ct);
            await context.Database.CloseConnectionAsync();
            await DropOutsideTablesAsync(context);
        }
    }

    [Fact]
    public async Task A_savepoint_of_the_caller_s_survives_a_reset_inside_its_transaction()
    {
        // MySQL replaces a savepoint of the same name, and the reset used one fixed name, then released it.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct))
        {
            await context.Database.ExecuteSqlRawAsync("SAVEPOINT ef_toolkit_reset", Ct);
            await context.Database.ResetDataAsync(cancellationToken: Ct);
            (await context.Customers.CountAsync(Ct)).ShouldBe(0);

            await context.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT ef_toolkit_reset", Ct);
            (await ShopData.CountsAsync(context)).ShouldBe(before);
            await transaction.CommitAsync(Ct);
        }

        (await ShopData.CountsAsync(context)).ShouldBe(before);
    }

    [Fact]
    public async Task With_checks_off_a_key_that_already_has_violations_is_refused_before_anything_is_deleted()
    {
        // A trigger could delete the old orphan and write a new one: the count stays one and verification
        // passed. With an orphan already there, nothing tells the two apart, so such a reset is refused.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await CreateOutsideTablesAsync(context);
        await context.Database.OpenConnectionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO OutChild VALUES (1, 2147483647)", Ct);
        await context.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 1", Ct);
        await context.Database.CloseConnectionAsync();
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER WriteOutside AFTER DELETE ON Categories FOR EACH ROW "
                + "BEGIN DELETE FROM OutChild WHERE Id = 1; INSERT IGNORE INTO OutChild VALUES (2, 2147483646); END", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(
                    () => context.Database.ResetDataAsync(o => o.RestartIdentity = false, Ct)))
                .Message.ShouldContain(".OutChild' already has rows that violate its foreign key");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT Id FROM OutChild")).ShouldBe(1);

            // Without a cycle the checks stay on, so the same reset runs: MySQL refuses any new orphan itself.
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER WriteOutside", Ct);
            await context.Database.ResetDataAsync(
                o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>().RestartIdentity = false, Ct);
            (await context.Customers.CountAsync(Ct)).ShouldBe(0);
        }
        finally
        {
            await DropOutsideTablesAsync(context);
        }
    }

    private async Task<ShopContext> LimitedUserAsync(ShopContext admin, string user, params string[] grants)
    {
        var drop = "DROP USER IF EXISTS '" + user + "'@'%'";
        var create = "CREATE USER '" + user + "'@'%' IDENTIFIED BY '" + user + "'";
        await admin.Database.ExecuteSqlRawAsync(drop, Ct);
        await admin.Database.ExecuteSqlRawAsync(create, Ct);
        foreach (var grant in grants)
        {
            var sql = grant.Replace("{db}", DatabaseName, StringComparison.Ordinal) + " TO '" + user + "'@'%'";
            await admin.Database.ExecuteSqlRawAsync(sql, Ct);
        }

        return LimitedContext(user);
    }

    [Theory]
    [InlineData("process")]
    [InlineData("partial-revoke")]
    public async Task A_global_privilege_that_does_not_show_every_table_does_not_pass_for_one(string kind)
    {
        // A global PROCESS shows no table; a global SELECT revoked for this database shows none of it either,
        // though information_schema still lists the global grant. Either way the cascade child stays hidden.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE HiddenChild (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO HiddenChild VALUES (1, {0})", [customer], Ct);
        var tables = await context.Database.SqlQueryRaw<string>(
                "SELECT TABLE_NAME AS Value FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'HiddenChild'")
            .ToListAsync(Ct);
        var tableGrants = tables.Select(t => "GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON `{db}`.`" + t + "`").ToList();
        if (kind == "partial-revoke")
        {
            await context.Database.ExecuteSqlRawAsync("SET GLOBAL partial_revokes = ON", Ct);
        }

        try
        {
            string[] grants = kind == "process"
                ? ["GRANT PROCESS ON *.*", .. tableGrants]
                : ["GRANT SELECT ON *.*", .. tableGrants];
            await using var limited = await LimitedUserAsync(context, "seeing_less", grants);
            if (kind == "partial-revoke")
            {
                var revoke = "REVOKE SELECT ON `" + DatabaseName + "`.* FROM 'seeing_less'@'%'";
                await context.Database.ExecuteSqlRawAsync(revoke, Ct);
            }

            (await ScalarAsync(limited, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_NAME = 'HiddenChild'")).ShouldBe(0);
            (await Should.ThrowAsync<InvalidOperationException>(() => limited.Database.ResetDataAsync(
                    o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>(), Ct)))
                .Message.ShouldContain("holds no privilege on the whole database");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM HiddenChild")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'seeing_less'@'%'", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS HiddenChild", Ct);
            if (kind == "partial-revoke")
            {
                await context.Database.ExecuteSqlRawAsync("SET GLOBAL partial_revokes = OFF", Ct);
            }
        }
    }

    [Fact]
    public async Task Under_partial_revokes_a_database_grant_s_underscore_is_not_a_wildcard()
    {
        // With partial_revokes on, MySQL reads `eftestin_` as that literal name: the grant covers nothing
        // here, though LIKE matched it to this database and let the hidden cascade child through.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        var customer = await context.Customers.Select(c => c.Id).FirstAsync(Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE HiddenChild (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id) ON DELETE CASCADE)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO HiddenChild VALUES (1, {0})", [customer], Ct);
        var tables = await context.Database.SqlQueryRaw<string>(
                "SELECT TABLE_NAME AS Value FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME <> 'HiddenChild'")
            .ToListAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SET GLOBAL partial_revokes = ON", Ct);
        try
        {
            string[] grants =
            [
                "GRANT SELECT ON `" + DatabaseName[..^1] + "_`.*",
                .. tables.Select(t => "GRANT SELECT, INSERT, UPDATE, DELETE, ALTER ON `{db}`.`" + t + "`"),
            ];
            await using var limited = await LimitedUserAsync(context, "literal_grant", grants);

            (await ScalarAsync(limited, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_NAME = 'HiddenChild'")).ShouldBe(0);
            (await Should.ThrowAsync<InvalidOperationException>(() => limited.Database.ResetDataAsync(
                    o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>(), Ct)))
                .Message.ShouldContain("holds no privilege on the whole database");

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            (await ScalarAsync(context, "SELECT COUNT(*) FROM HiddenChild")).ShouldBe(1);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'literal_grant'@'%'", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS HiddenChild", Ct);
            await context.Database.ExecuteSqlRawAsync("SET GLOBAL partial_revokes = OFF", Ct);
        }
    }

    [Fact]
    public async Task A_view_under_a_mapped_table_s_name_is_refused_and_its_base_table_keeps_its_rows()
    {
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var tags = await context.Tags.CountAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("RENAME TABLE Tags TO TagsBase", Ct);
        await context.Database.ExecuteSqlRawAsync("CREATE VIEW Tags AS SELECT * FROM TagsBase", Ct);
        try
        {
            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("'Tags': the model maps it as a table, but in the database it is a view");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM TagsBase")).ShouldBe(tags);
            (await context.Customers.CountAsync(Ct)).ShouldBeGreaterThan(0);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP VIEW IF EXISTS Tags", Ct);
            await context.Database.ExecuteSqlRawAsync("RENAME TABLE TagsBase TO Tags", Ct);
        }
    }

    [Fact]
    public async Task A_counter_restart_that_fails_after_the_commit_says_the_rows_are_gone()
    {
        // Without ALTER, the deletes committed and only "ALTER command denied" came back.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        try
        {
            await using var limited = await LimitedUserAsync(context, "no_alter", "GRANT SELECT, INSERT, UPDATE, DELETE ON `{db}`.*");

            var failure = await Should.ThrowAsync<InvalidOperationException>(() => limited.Database.ResetDataAsync(cancellationToken: Ct));
            failure.Message.ShouldContain("deleted the rows and committed");
            failure.InnerException.ShouldBeAssignableTo<DbException>();

            (await ShopData.CountsAsync(context)).Values.ShouldAllBe(v => v == 0);
            await AssertConstraintsRestoredAsync(limited);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP USER IF EXISTS 'no_alter'@'%'", Ct);
        }
    }

    private sealed class Slashed
    {
        public int Id { get; set; }
    }

    private sealed class SlashedContext(DbContextOptions<SlashedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Slashed>().ToTable("Par\\ent");
    }

    [Fact]
    public async Task Under_NO_BACKSLASH_ESCAPES_a_table_name_with_a_backslash_is_still_matched_in_the_catalog()
    {
        // Doubling the backslash, right under the default sql_mode, named another table under this one:
        // the preflight missed SlashChild and the truncate orphaned it.
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = new SlashedContext(
            new DbContextOptionsBuilder<SlashedContext>().UseMySQL(Fixture.ConnectionString).Options);
        await context.Database.ExecuteSqlRawAsync("CREATE TABLE `Par\\ent` (Id int PRIMARY KEY)", Ct);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE SlashChild (Id int PRIMARY KEY, ParentId int NOT NULL, FOREIGN KEY (ParentId) REFERENCES `Par\\ent` (Id))", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO `Par\\ent` VALUES (1)", Ct);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO SlashChild VALUES (1, 1)", Ct);
        try
        {
            await context.Database.OpenConnectionAsync(Ct);
            await context.Database.ExecuteSqlRawAsync("SET SESSION sql_mode = 'NO_BACKSLASH_ESCAPES'", Ct);

            (await Should.ThrowAsync<InvalidOperationException>(() => context.Database.ResetDataAsync(cancellationToken: Ct)))
                .Message.ShouldContain("'SlashChild', which the reset does not empty");

            (await ScalarAsync(context, "SELECT COUNT(*) FROM `Par\\ent`")).ShouldBe(1);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS SlashChild", Ct);
            await context.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `Par\\ent`", Ct);
        }
    }

    [Fact]
    public async Task Without_a_cycle_the_deletes_keep_checks_on_and_the_orphaning_trigger_fails_at_once()
    {
        Assert.SkipWhen(Fixture.SkipReason is not null, Fixture.SkipReason ?? "");
        await using var context = Fixture.CreateContext();
        await ShopData.SeedEverythingAsync(context);
        var before = await ShopData.CountsAsync(context);
        await context.Database.ExecuteSqlRawAsync(LeaveOrphan, Ct);
        try
        {
            await Should.ThrowAsync<DbException>(() => context.Database.ResetDataAsync(
                o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>().RestartIdentity = false,
                Ct));

            (await ShopData.CountsAsync(context)).ShouldBe(before);
            await AssertConstraintsRestoredAsync(context);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS LeaveOrphan", Ct);
        }

        // And without the trigger, the same reset empties what it was asked to.
        await context.Database.ResetDataAsync(
            o => o.ExcludeEntity<Category>().ExcludeEntity<Team>().ExcludeEntity<Player>().RestartIdentity = false, Ct);
        (await context.OrderLines.CountAsync(Ct)).ShouldBe(0);
        (await context.Categories.CountAsync(Ct)).ShouldBeGreaterThan(0);
    }

    [Fact]
    public Task A_table_outside_the_model_that_references_a_reset_one_stops_the_reset()
        // FOREIGN_KEY_CHECKS = 0 would let the truncate through, so the catalog check refuses first.
        => AssertUnmappedReferenceStopsResetAsync<InvalidOperationException>(
            "CREATE TABLE Unmapped (Id int PRIMARY KEY, CustomerId int NOT NULL, FOREIGN KEY (CustomerId) REFERENCES Customers (Id))",
            "DROP TABLE Unmapped");

    protected override async Task AssertConstraintsRestoredAsync(ShopContext context)
        => (await ScalarAsync(context, "SELECT @@FOREIGN_KEY_CHECKS")).ShouldBe(1);
}
