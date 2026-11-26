using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Testcontainers.MsSql;

namespace EFToolkit.Sql.Integration.Infrastructure;

/// <summary>One SQL Server 2022 database, for the security-policy behaviour only SQL Server has.</summary>
/// <remarks>
///     SQL Server applies a security policy to every login, sysadmin included, so one connection both
///     creates the policy and is subject to it.
/// </remarks>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? _container;

    /// <summary>Set when Docker or SQL Server is unavailable; every scenario skips rather than fails.</summary>
    public string? SkipReason { get; private set; }

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var startup = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(2);
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            using var cts = new CancellationTokenSource(startup);
            await _container.StartAsync(cts.Token);
        }
#pragma warning disable CA1031 // Any failure to start the engine means it cannot run here.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SkipReason = $"SQL Server could not be started in Docker: {ex.GetType().Name}: {ex.Message}";
            return;
        }

        ConnectionString = new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "efsql" }.ConnectionString;
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public AppDb CreateContext(Action<DbContextOptionsBuilder<AppDb>>? configure = null)
    {
        var builder = new DbContextOptionsBuilder<AppDb>()
            .UseSqlServer(ConnectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        configure?.Invoke(builder);
        return new AppDb(builder.Options);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}

[CollectionDefinition("sql-sqlserver")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;
