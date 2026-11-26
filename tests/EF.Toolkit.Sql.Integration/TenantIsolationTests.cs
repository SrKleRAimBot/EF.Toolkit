using EFToolkit.Sql.Integration.Infrastructure;
using EFToolkit.Sql.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace EFToolkit.Sql.Integration;

/// <summary>Row-level security end to end, as the ordinary role that owns the table.</summary>
[Trait("Engine", "postgres17")]
[Collection("sql-postgres17")]
public sealed class TenantIsolationTests(PostgreSqlFixture fixture) : IAsyncLifetime
{
    private static readonly TenantIsolationPolicy Isolation = new("Documents", "TenantId", "app.tenant_id", "integer");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await fixture.ResetAsync();

        // Seeded as the superuser, which row-level security never applies to.
        await using var admin = fixture.CreateAdminContext();
        admin.Documents.AddRange(
            new Document { TenantId = 1, Title = "one-a" },
            new Document { TenantId = 1, Title = "one-b" },
            new Document { TenantId = 2, Title = "two-a" });
        await admin.SaveChangesAsync(Token);
        await admin.Database.CreateDatabaseObjectsAsync(Isolation);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Without_isolation_the_owning_role_sees_every_tenant()
    {
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.DropDatabaseObjectsAsync(Isolation);

        await using var context = AppContext(() => null);
        (await context.Documents.CountAsync(Token)).ShouldBe(3);
    }

    [Fact]
    public async Task Another_permissive_policy_cannot_widen_a_tenant_past_its_own_rows()
    {
        // PostgreSQL ORs permissive policies together. With the tenant check permissive, this
        // reporting policy would let every session — even one with no tenant — read every row.
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE POLICY reporting ON \"Documents\" AS PERMISSIVE FOR SELECT USING (true)", Token);
        try
        {
            await using (var none = AppContext(() => null))
            {
                (await none.Documents.CountAsync(Token)).ShouldBe(0);
            }

            await using var one = AppContext(() => 1);
            (await TitlesAsync(one)).ShouldBe(["one-a", "one-b"]);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP POLICY IF EXISTS reporting ON \"Documents\"", Token);
        }
    }

    [Fact]
    public async Task Dropping_isolation_leaves_another_policy_on_the_table_enforced()
    {
        // A policy the table already had must not be left defined but switched off by a rollback.
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE POLICY tenant_one_only ON \"Documents\" AS PERMISSIVE USING (\"TenantId\" = 1)", Token);
        try
        {
            await admin.Database.DropDatabaseObjectsAsync(Isolation);

            await using var context = AppContext(() => null);
            (await TitlesAsync(context)).ShouldBe(["one-a", "one-b"]);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP POLICY IF EXISTS tenant_one_only ON \"Documents\"", Token);
        }
    }

    [Fact]
    public async Task Dropping_isolation_checks_the_table_search_path_resolves_to()
    {
        // search_path = app, public with no app."Documents": the unqualified name resolves to
        // public."Documents", which has a policy of its own. A check against current_schema() would look
        // in app, find nothing, and switch off row-level security on the table that does have one.
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.OpenConnectionAsync(Token);
        await admin.Database.ExecuteSqlRawAsync("CREATE SCHEMA IF NOT EXISTS app", Token);
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE POLICY tenant_one_only ON public.\"Documents\" AS PERMISSIVE USING (\"TenantId\" = 1)", Token);
        try
        {
            await admin.Database.ExecuteSqlRawAsync("SET search_path = app, public", Token);
            await admin.Database.DropDatabaseObjectsAsync(Isolation);

            await using var context = AppContext(() => null);
            (await TitlesAsync(context)).ShouldBe(["one-a", "one-b"]);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("SET search_path = public", Token);
            await admin.Database.ExecuteSqlRawAsync("DROP POLICY IF EXISTS tenant_one_only ON public.\"Documents\"", Token);
            await admin.Database.ExecuteSqlRawAsync("DROP SCHEMA IF EXISTS app", Token);
        }
    }

    [Fact]
    public async Task Without_the_base_grant_the_tables_own_policies_still_decide_within_a_tenant()
    {
        // The table already limits every role to titles ending '-a'. The blanket base grant would widen
        // that to every row of the tenant; grantAccess: false leaves the existing rule in charge.
        var narrow = new TenantIsolationPolicy("Documents", "TenantId", "app.tenant_id", "integer", grantAccess: false);

        await using var admin = fixture.CreateAdminContext();
        await admin.Database.DropDatabaseObjectsAsync(Isolation);
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE POLICY a_titles_only ON \"Documents\" AS PERMISSIVE USING (\"Title\" LIKE '%-a')", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(narrow);

            await using (var one = AppContext(() => 1))
            {
                (await TitlesAsync(one)).ShouldBe(["one-a"]);
            }

            await using var none = AppContext(() => null);
            (await none.Documents.CountAsync(Token)).ShouldBe(0);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(narrow);
            await admin.Database.ExecuteSqlRawAsync("DROP POLICY IF EXISTS a_titles_only ON \"Documents\"", Token);
        }
    }

    [Fact]
    public async Task Reapplying_without_the_base_grant_withdraws_the_one_an_earlier_install_left()
    {
        // Installed with the grant, then reapplied without it: the old "_access" policy stayed and went on
        // showing every row of the tenant past the table's own permissive policy.
        var narrow = new TenantIsolationPolicy("Documents", "TenantId", "app.tenant_id", "integer", grantAccess: false);

        await using var admin = fixture.CreateAdminContext();
        await admin.Database.CreateDatabaseObjectsAsync(Isolation);
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE POLICY a_titles_only ON \"Documents\" AS PERMISSIVE USING (\"Title\" LIKE '%-a')", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(narrow);

            await using var one = AppContext(() => 1);
            (await TitlesAsync(one)).ShouldBe(["one-a"]);
        }
        finally
        {
            await admin.Database.DropDatabaseObjectsAsync(narrow);
            await admin.Database.ExecuteSqlRawAsync("DROP POLICY IF EXISTS a_titles_only ON \"Documents\"", Token);
            await admin.Database.CreateDatabaseObjectsAsync(Isolation);
        }
    }

    [Fact]
    public async Task A_table_name_containing_dollar_quotes_creates_and_drops_cleanly()
    {
        var odd = new TenantIsolationPolicy("odd$$name", "TenantId", "app.tenant_id", "integer", policyName: "odd_iso");

        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync("CREATE TABLE \"odd$$name\" (\"Id\" int PRIMARY KEY, \"TenantId\" int NOT NULL)", Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(odd);
            await admin.Database.DropDatabaseObjectsAsync(odd);

            var enabled = await admin.Database
                .SqlQueryRaw<bool>("SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE relname = 'odd$$name'")
                .SingleAsync(Token);
            enabled.ShouldBeFalse();
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"odd$$name\"", Token);
        }
    }

    [Fact]
    public async Task A_replacement_that_fails_leaves_the_original_policy_enforcing()
    {
        // Replacing drops the restrictive policy before recreating it. Committed piecemeal, a failure
        // there left only the permissive USING (true) grant — every session read every tenant.
        var broken = new TenantIsolationPolicy("Documents", "TenantId", "app.tenant_id", "no_such_type");

        await using var admin = fixture.CreateAdminContext();
        await Should.ThrowAsync<PostgresException>(() => admin.Database.CreateDatabaseObjectsAsync(broken));

        await using (var none = AppContext(() => null))
        {
            (await none.Documents.CountAsync(Token)).ShouldBe(0);
        }

        await using var one = AppContext(() => 1);
        (await TitlesAsync(one)).ShouldBe(["one-a", "one-b"]);
    }

    [Fact]
    public async Task Inside_a_callers_transaction_a_failed_replacement_leaves_it_usable_and_the_policy_intact()
    {
        // PostgreSQL aborts a transaction on any error; rolling back to the savepoint is what lets the
        // caller carry on and commit, with the original policy still in place.
        var broken = new TenantIsolationPolicy("Documents", "TenantId", "app.tenant_id", "no_such_type");

        await using var admin = fixture.CreateAdminContext();
        await using (var transaction = await admin.Database.BeginTransactionAsync(Token))
        {
            await Should.ThrowAsync<PostgresException>(() => admin.Database.CreateDatabaseObjectsAsync(broken));
            (await admin.Documents.CountAsync(Token)).ShouldBe(3);
            await transaction.CommitAsync(Token);
        }

        await using var one = AppContext(() => 1);
        (await TitlesAsync(one)).ShouldBe(["one-a", "one-b"]);
    }

    [Fact]
    public async Task A_resolver_that_fails_once_does_not_leave_the_next_query_without_its_tenant()
    {
        // The physical open had succeeded before the resolver threw. Left open, EF reused the connection
        // without calling the interceptor again, and the retry ran with no tenant — and saw no rows.
        var calls = 0;
        await using var context = AppContext(() => ++calls == 1 ? throw new InvalidOperationException("no tenant yet") : 1);

        await Should.ThrowAsync<InvalidOperationException>(() => context.Documents.CountAsync(Token));

        (await TitlesAsync(context)).ShouldBe(["one-a", "one-b"]);

        calls = 0;
        Should.Throw<InvalidOperationException>(() => context.Documents.Count());
        context.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_session_value_longer_than_the_tenant_column_reaches_no_tenant()
    {
        // Casting the session value to varchar(8) truncated 'acme-dev2' to 'acme-dev', so the longer
        // value read, changed and deleted the shorter tenant's rows.
        var labels = new TenantIsolationPolicy("Labels", "Tenant", "app.tenant_key", "varchar(8)", policyName: "labels_iso");
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Labels\" (\"Id\" int PRIMARY KEY, \"Tenant\" varchar(8) NOT NULL); "
                + "INSERT INTO \"Labels\" VALUES (1, 'acme-dev'); "
                + $"ALTER TABLE \"Labels\" OWNER TO {PostgreSqlFixture.AppUser};",
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(labels);

            async Task<long> CountAs(string tenant, string sql)
            {
                await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString)
                    .UseSessionContext(s => s.Set("app.tenant_key", () => tenant))
                    .Options);
                return sql.StartsWith("SELECT", StringComparison.Ordinal)
                    ? await context.Database.SqlQueryRaw<long>(sql).SingleAsync(Token)
                    : await context.Database.ExecuteSqlRawAsync(sql, Token);
            }

            (await CountAs("acme-dev", "SELECT COUNT(*) AS \"Value\" FROM \"Labels\"")).ShouldBe(1);
            (await CountAs("acme-dev2", "SELECT COUNT(*) AS \"Value\" FROM \"Labels\"")).ShouldBe(0);
            (await CountAs("acme-dev2", "UPDATE \"Labels\" SET \"Id\" = 2")).ShouldBe(0);
            (await CountAs("acme-dev2", "DELETE FROM \"Labels\"")).ShouldBe(0);
            (await CountAs("acme-dev", "SELECT COUNT(*) AS \"Value\" FROM \"Labels\" WHERE \"Id\" = 1")).ShouldBe(1);
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Labels\"", Token);
        }
    }

    [Theory]
    [InlineData("numeric(10,2)")]
    [InlineData("pg_catalog.numeric(10,2)")]
    public async Task A_numeric_tenant_column_accepts_added_scale_but_not_rounding(string storeType)
    {
        // numeric(10,2) renders 12.5 back as 12.50: the same value, which a text comparison refused.
        // Rounding 12.555 to 12.56 is a different tenant and must still reach nothing.
        var accounts = new TenantIsolationPolicy("Accounts", "Tenant", "app.tenant_num", storeType, policyName: "accounts_iso");
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Accounts\" (\"Id\" int PRIMARY KEY, \"Tenant\" numeric(10,2) NOT NULL); "
                + "INSERT INTO \"Accounts\" VALUES (1, 12.50), (2, 12.56); "
                + $"ALTER TABLE \"Accounts\" OWNER TO {PostgreSqlFixture.AppUser};",
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(accounts);

            async Task<List<int>> IdsAs(decimal tenant)
            {
                await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString)
                    .UseSessionContext(s => s.Set("app.tenant_num", () => tenant))
                    .Options);
                return await context.Database.SqlQueryRaw<int>("SELECT \"Id\" AS \"Value\" FROM \"Accounts\"").ToListAsync(Token);
            }

            (await IdsAs(12.5m)).ShouldBe([1]);
            (await IdsAs(12.555m)).ShouldBeEmpty();
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Accounts\"", Token);
        }
    }

    [Fact]
    public async Task A_timestamp_tenant_matches_the_session_setter_s_own_format_but_not_a_rounded_one()
    {
        // The setter writes a DateTime as ISO 8601 ("2026-11-26T12:30:00.0000000"), which PostgreSQL
        // prints back as "2026-11-26 12:30:00" — the same instant. timestamp(0) rounding away a fraction
        // is a different tenant.
        var tenant = new DateTime(2026, 11, 26, 12, 30, 0, DateTimeKind.Unspecified);
        var cohorts = new TenantIsolationPolicy("Cohorts", "Tenant", "app.tenant_at", "timestamp(0)", policyName: "cohorts_iso");
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Cohorts\" (\"Id\" int PRIMARY KEY, \"Tenant\" timestamp(0) NOT NULL); "
                + "INSERT INTO \"Cohorts\" VALUES (1, '2026-11-26 12:30:00'); "
                + $"ALTER TABLE \"Cohorts\" OWNER TO {PostgreSqlFixture.AppUser};",
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(cohorts);

            async Task<List<int>> IdsAs(DateTime at)
            {
                await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString)
                    .UseSessionContext(s => s.Set("app.tenant_at", () => at))
                    .Options);
                return await context.Database.SqlQueryRaw<int>("SELECT \"Id\" AS \"Value\" FROM \"Cohorts\"").ToListAsync(Token);
            }

            (await IdsAs(tenant)).ShouldBe([1]);
            (await IdsAs(tenant.AddMilliseconds(400))).ShouldBeEmpty();
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Cohorts\"", Token);
        }
    }

    [Fact]
    public async Task A_bare_character_tenant_is_one_character_long_and_a_longer_session_value_reaches_nothing()
    {
        // character with no length is character(1): 'acme' casts to 'a'.
        var desks = new TenantIsolationPolicy("Desks", "Tenant", "app.tenant_chr", "character", policyName: "desks_iso");
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"Desks\" (\"Id\" int PRIMARY KEY, \"Tenant\" character NOT NULL); "
                + "INSERT INTO \"Desks\" VALUES (1, 'a'); "
                + $"ALTER TABLE \"Desks\" OWNER TO {PostgreSqlFixture.AppUser};",
            Token);
        try
        {
            await admin.Database.CreateDatabaseObjectsAsync(desks);

            async Task<List<int>> IdsAs(string tenant)
            {
                await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString)
                    .UseSessionContext(s => s.Set("app.tenant_chr", () => tenant))
                    .Options);
                return await context.Database.SqlQueryRaw<int>("SELECT \"Id\" AS \"Value\" FROM \"Desks\"").ToListAsync(Token);
            }

            (await IdsAs("a")).ShouldBe([1]);
            (await IdsAs("acme")).ShouldBeEmpty();
        }
        finally
        {
            await admin.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS \"Desks\"", Token);
        }
    }

    [Fact]
    public async Task Each_tenant_sees_only_its_own_rows()
    {
        await using (var one = AppContext(() => 1))
        {
            (await TitlesAsync(one)).ShouldBe(["one-a", "one-b"]);
        }

        await using (var two = AppContext(() => 2))
        {
            (await TitlesAsync(two)).ShouldBe(["two-a"]);
        }
    }

    [Fact]
    public async Task No_tenant_sees_nothing()
    {
        await using var context = AppContext(() => null);

        (await context.Documents.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task The_value_is_reapplied_every_time_the_connection_opens()
    {
        var tenant = 1;
        await using var context = AppContext(() => tenant);

        // EF opens and closes the connection around each query, and the pool resets the session in
        // between; each query must still be filtered, and by the tenant current at that moment.
        (await context.Documents.CountAsync(Token)).ShouldBe(2);
        (await context.Documents.CountAsync(Token)).ShouldBe(2);
        tenant = 2;
        (await context.Documents.CountAsync(Token)).ShouldBe(1);

        // And through the synchronous open path.
        tenant = 1;
        context.Documents.Count().ShouldBe(2);
    }

    [Fact]
    public async Task A_tenant_can_write_its_own_rows()
    {
        await using var context = AppContext(() => 2);

        context.Documents.Add(new Document { TenantId = 2, Title = "two-b" });
        await context.SaveChangesAsync(Token);

        (await TitlesAsync(context)).ShouldBe(["two-a", "two-b"]);
    }

    [Fact]
    public async Task A_tenant_cannot_insert_into_another_tenant()
    {
        await using var context = AppContext(() => 1);

        context.Documents.Add(new Document { TenantId = 2, Title = "smuggled" });
        var ex = await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));

        ex.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe("42501");
        await using var admin = fixture.CreateAdminContext();
        (await admin.Documents.CountAsync(d => d.Title == "smuggled", Token)).ShouldBe(0);
    }

    [Fact]
    public async Task A_tenant_cannot_move_its_row_to_another_tenant()
    {
        await using var context = AppContext(() => 1);
        var document = await context.Documents.FirstAsync(Token);

        document.TenantId = 2;
        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync(Token));
    }

    [Fact]
    public async Task A_tenant_cannot_change_or_delete_another_tenants_rows()
    {
        await using var context = AppContext(() => 1);

        (await context.Documents.Where(d => d.TenantId == 2).ExecuteUpdateAsync(s => s.SetProperty(d => d.Title, "x"), Token)).ShouldBe(0);
        (await context.Documents.Where(d => d.TenantId == 2).ExecuteDeleteAsync(Token)).ShouldBe(0);

        await using var admin = fixture.CreateAdminContext();
        (await admin.Documents.SingleAsync(d => d.TenantId == 2, Token)).Title.ShouldBe("two-a");
    }

    [Fact]
    public async Task Values_resolve_from_the_request_scope_under_AddDbContext()
    {
        var services = new ServiceCollection()
            .AddScoped<CurrentTenant>()
            .AddDbContext<AppDb>(o => o
                .UseNpgsql(fixture.AppConnectionString)
                .UseSessionContext(s => s.Set("app.tenant_id", sp => sp.GetRequiredService<CurrentTenant>().Id)))
            .BuildServiceProvider(validateScopes: true);
        await using var _ = services;

        foreach (var (tenant, expected) in new[] { (1, 2), (2, 1) })
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().Id = tenant;

            var context = scope.ServiceProvider.GetRequiredService<AppDb>();
            (await context.Documents.CountAsync(Token)).ShouldBe(expected);
        }
    }

    [Fact]
    public async Task Setting_several_values_needs_one_close_and_a_failed_setter_leaves_nothing_open()
    {
        await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString).Options);

        await context.Database.SetSessionContextAsync("app.tenant_id", 1, Token);
        await context.Database.SetSessionContextAsync("app.other", "x", Token);
        (await context.Documents.CountAsync(Token)).ShouldBe(2);

        await context.Database.CloseConnectionAsync();
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);

        // A command that fails after the open must not leave that open behind.
        await using var failing = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString)
            .AddInterceptors(new FailSetConfig())
            .Options);
        await Should.ThrowAsync<InvalidOperationException>(() => failing.Database.SetSessionContextAsync("app.tenant_id", 1, Token));
        failing.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
    }

    private sealed class FailSetConfig : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => command.CommandText.Contains("set_config", StringComparison.Ordinal)
                ? throw new InvalidOperationException("set_config failed")
                : base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    [Fact]
    public async Task SetSessionContext_holds_for_the_rest_of_the_contexts_life()
    {
        await using var context = new AppDb(PostgreSqlFixture.Options(fixture.AppConnectionString).Options);

        (await context.Documents.CountAsync(Token)).ShouldBe(0);

        await context.Database.SetSessionContextAsync("app.tenant_id", 2, Token);
        (await TitlesAsync(context)).ShouldBe(["two-a"]);
        (await context.Documents.CountAsync(Token)).ShouldBe(1);

        await context.Database.SetSessionContextAsync("app.tenant_id", null, Token);
        (await context.Documents.CountAsync(Token)).ShouldBe(0);
    }

    [Fact]
    public async Task The_session_value_is_what_set_config_stored()
    {
        await using var context = AppContext(() => 7);

        (await context.Database.SqlQueryRaw<string>("SELECT current_setting('app.tenant_id') AS \"Value\"").SingleAsync(Token))
            .ShouldBe("7");
    }

    private AppDb AppContext(Func<object?> tenant)
        => new(PostgreSqlFixture.Options(fixture.AppConnectionString)
            .UseSessionContext(s => s.Set("app.tenant_id", tenant))
            .Options);

    private static Task<List<string>> TitlesAsync(AppDb context)
        => context.Documents.OrderBy(d => d.Title).Select(d => d.Title).ToListAsync(Token);

    private sealed class CurrentTenant
    {
        public int Id { get; set; }
    }
}
