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
