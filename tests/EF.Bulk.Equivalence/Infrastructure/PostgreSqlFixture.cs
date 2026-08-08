using EFBulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EFBulk.Equivalence.Infrastructure;

/// <summary>Base fixture for PostgreSQL, parameterised by image tag.</summary>
public abstract class PostgreSqlFixture : DatabaseFixture
{
    private PostgreSqlContainer? _container;

    /// <summary>The Docker image to run, e.g. <c>postgres:17-alpine</c>.</summary>
    protected abstract string Image { get; }

    protected override async Task StartContainerAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithImage(Image)
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

    protected override async Task ResetAsync(string connectionString)
    {
        var tables = string.Join(
            ", ",
            MappedTables().Select(t => t.Schema is null
                ? $"\"{t.Name}\""
                : $"\"{t.Schema}\".\"{t.Name}\""));

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // RESTART IDENTITY resets the backing sequences, which the harness compares.
        command.CommandText = $"TRUNCATE {tables} RESTART IDENTITY CASCADE";
        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString)
        => builder.UseNpgsql(connectionString);

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

/// <summary>PostgreSQL 16 — the version where <c>MERGE ... RETURNING</c> is not available.</summary>
public sealed class PostgreSql16Fixture : PostgreSqlFixture
{
    public override string Engine => "postgres16";
    protected override string Image => "postgres:16-alpine";
}

/// <summary>PostgreSQL 17 — supports <c>MERGE ... RETURNING</c>, so staged inserts can correlate exactly.</summary>
public sealed class PostgreSql17Fixture : PostgreSqlFixture
{
    public override string Engine => "postgres17";
    protected override string Image => "postgres:17-alpine";
}
