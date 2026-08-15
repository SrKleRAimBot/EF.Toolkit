using System.Data.Common;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>Base fixture for PostgreSQL, parameterised by image tag.</summary>
public abstract class PostgreSqlAuditFixture : AuditDatabaseFixture
{
    private PostgreSqlContainer? _container;

    /// <summary>The Docker image to run, e.g. <c>postgres:17-alpine</c>.</summary>
    protected abstract string Image { get; }

    protected override async Task StartContainerAsync()
    {
        _container = new PostgreSqlBuilder(Image)
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();
    }

    protected override async Task<string> CreateDatabaseAsync(string databaseName)
    {
        var adminConnectionString = _container!.GetConnectionString();

        await using (var connection = new NpgsqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Identifier is a test-controlled constant, not user input.
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName
        }.ConnectionString;
    }

    public override async Task ResetAsync()
    {
        var tables = string.Join(
            ", ",
            MappedTables().Select(t => t.Schema is null
                ? $"\"{t.Name}\""
                : $"\"{t.Schema}\".\"{t.Name}\""));

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = $"TRUNCATE {tables} RESTART IDENTITY CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    public override DbConnection OpenConnection()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public override string Quote(string identifier) => $"\"{identifier}\"";

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString,
        bool retryOnFailure)
        => builder.UseNpgsql(
            connectionString,
            o =>
            {
                if (retryOnFailure)
                {
                    o.EnableRetryOnFailure();
                }
            });

    protected override void ConfigureAuditing(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<AuditOptionsBuilder> configure)
        => builder.UseNpgsqlAuditing(a => configure(a));

    protected override void ConfigureBulk(DbContextOptionsBuilder<ShopContext> builder)
        => builder.UseNpgsqlBulk();

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>PostgreSQL 16 — no <c>MERGE ... RETURNING</c>, so upserts take the ON CONFLICT path.</summary>
public sealed class PostgreSql16AuditFixture : PostgreSqlAuditFixture
{
    public override string Engine => "postgres16";
    protected override string Image => "postgres:16-alpine";
}

/// <summary>PostgreSQL 17 — upserts take the <c>MERGE</c> path.</summary>
public sealed class PostgreSql17AuditFixture : PostgreSqlAuditFixture
{
    public override string Engine => "postgres17";
    protected override string Image => "postgres:17-alpine";
}
