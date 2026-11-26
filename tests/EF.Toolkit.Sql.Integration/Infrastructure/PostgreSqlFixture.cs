using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EFToolkit.Sql.Integration.Infrastructure;

/// <summary>One PostgreSQL 17 database, with an ordinary role for the row-level security scenarios.</summary>
/// <remarks>
///     Two connection strings: the superuser that owns the schema and creates the objects, and
///     <c>app_user</c>, a login with no superuser or <c>BYPASSRLS</c> — the only kind of role row-level
///     security applies to. <c>app_user</c> also owns the <c>Documents</c> table, so the RLS scenarios
///     prove <c>FORCE</c> as well: without it the owner would see every tenant.
/// </remarks>
public sealed class PostgreSqlFixture : IAsyncLifetime
{
    public const string AppUser = "app_user";
    private const string AppPassword = "app_password";

    private PostgreSqlContainer? _container;

    /// <summary>Set when Docker is unavailable; every scenario skips rather than fails.</summary>
    public string? SkipReason { get; private set; }

    public string AdminConnectionString { get; private set; } = "";

    public string AppConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("postgres")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means the engine cannot run here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"PostgreSQL could not be started in Docker: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        var server = _container.GetConnectionString();
        await ExecuteAsync(server, "CREATE DATABASE efsql");

        AdminConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = "efsql" }.ConnectionString;
        AppConnectionString = new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Username = AppUser,
            Password = AppPassword,
        }.ConnectionString;

        await using (var context = CreateAdminContext())
        {
            await context.Database.EnsureCreatedAsync();
        }

        await ExecuteAsync(
            AdminConnectionString,
            $"CREATE ROLE {AppUser} LOGIN PASSWORD '{AppPassword}' NOSUPERUSER NOBYPASSRLS",
            $"GRANT USAGE ON SCHEMA public TO {AppUser}",
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppUser}",
            $"GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO {AppUser}",
            $"ALTER TABLE \"Documents\" OWNER TO {AppUser}");
    }

    public AppDb CreateAdminContext() => new(Options(AdminConnectionString).Options);

    public static DbContextOptionsBuilder<AppDb> Options(string connectionString)
        => new DbContextOptionsBuilder<AppDb>()
            .UseNpgsql(connectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));

    public async Task ResetAsync()
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

        await ExecuteAsync(
            AdminConnectionString,
            "TRUNCATE \"Orders\", \"Documents\" RESTART IDENTITY",
            "DROP MATERIALIZED VIEW IF EXISTS order_totals",
            "DROP VIEW IF EXISTS big_orders",
            "DROP POLICY IF EXISTS \"Documents_tenant_isolation\" ON \"Documents\"",
            "ALTER TABLE \"Documents\" NO FORCE ROW LEVEL SECURITY",
            "ALTER TABLE \"Documents\" DISABLE ROW LEVEL SECURITY");
    }

    public static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-controlled constants.
            command.CommandText = statement;
#pragma warning restore CA2100
            await command.ExecuteNonQueryAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("sql-postgres17")]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>;
