using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace EFToolkit.Sql.Integration.Infrastructure;

/// <summary>One MySQL 8.4 database, for the behaviour that differs because MySQL commits DDL implicitly.</summary>
public sealed class MySqlFixture : IAsyncLifetime
{
    private MySqlContainer? _container;

    /// <summary>Set when Docker is unavailable; every scenario skips rather than fails.</summary>
    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MySqlBuilder("mysql:8.4").WithUsername("root").WithPassword("mysql").Build();
            await _container.StartAsync();
        }
#pragma warning disable CA1031 // Any failure to start Docker means the engine cannot run here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"MySQL could not be started in Docker: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        ConnectionString = new MySqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = "efsql",

            // The container's root account authenticates with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        }.ConnectionString;

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public AppDb CreateContext()
        => new(new DbContextOptionsBuilder<AppDb>()
            .UseMySQL(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options);

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("sql-mysql")]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>;
