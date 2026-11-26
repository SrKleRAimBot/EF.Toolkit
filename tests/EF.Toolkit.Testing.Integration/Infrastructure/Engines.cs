using System.Runtime.InteropServices;
using EFToolkit.Testing.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace EFToolkit.Testing.Integration.Infrastructure;

public sealed class PostgreSqlFixture : EngineFixture
{
    private PostgreSqlContainer? _container;

    public override string Engine => "postgres17";

    protected override async Task StartContainerAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();
    }

    protected override string ConnectionStringFor(string databaseName)
        => new NpgsqlConnectionStringBuilder(_container!.GetConnectionString()) { Database = databaseName }.ConnectionString;

    protected override void ConfigureProvider(DbContextOptionsBuilder<ShopContext> builder, string connectionString)
        => builder.UseNpgsql(connectionString);

    protected override async ValueTask DisposeContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>SQL Server 2022.</summary>
/// <remarks>
///     Microsoft publishes no arm64 image, but the amd64 one runs under Rosetta on Apple Silicon —
///     just slowly to start. Skipping on architecture alone would mean developing against this
///     engine blind, so only a container that genuinely will not start skips the suite.
/// </remarks>
public sealed class SqlServerFixture : EngineFixture
{
    private MsSqlContainer? _container;

    public override string Engine => "sqlserver";

    protected override async Task StartContainerAsync()
    {
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

        var startupTimeout = RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromMinutes(2);

        using var cts = new CancellationTokenSource(startupTimeout);

        try
        {
            await _container.StartAsync(cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            SkipReason = $"SQL Server did not start within {startupTimeout.TotalMinutes:F0} minute(s)"
                + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? " under arm64 emulation." : ".");
        }
    }

    protected override string ConnectionStringFor(string databaseName)
        => new SqlConnectionStringBuilder(_container!.GetConnectionString()) { InitialCatalog = databaseName }.ConnectionString;

    protected override void ConfigureProvider(DbContextOptionsBuilder<ShopContext> builder, string connectionString)
        => builder.UseSqlServer(connectionString);

    protected override async ValueTask DisposeContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

/// <summary>MySQL 8.4, through Oracle's MySql.EntityFrameworkCore provider.</summary>
public sealed class MySqlFixture : EngineFixture
{
    private MySqlContainer? _container;

    public override string Engine => "mysql";

    protected override async Task StartContainerAsync()
    {
        _container = new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword("mysql")
            .Build();

        await _container.StartAsync();
    }

    protected override string ConnectionStringFor(string databaseName)
        => new MySqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            Database = databaseName,

            // The container's root account authenticates with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        }.ConnectionString;

    protected override void ConfigureProvider(DbContextOptionsBuilder<ShopContext> builder, string connectionString)
        => builder.UseMySQL(connectionString);

    protected override async ValueTask DisposeContainerAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
