using EFToolkit.Query.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace EFToolkit.Query.Equivalence.Infrastructure;

/// <summary>MySQL 8.4, through Oracle's MySql.EntityFrameworkCore provider.</summary>
public sealed class MySqlQueryFixture : QueryDatabaseFixture
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

    protected override async Task<string> CreateDatabaseAsync(string databaseName)
    {
        var adminConnectionString = new MySqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            // The container's root account authenticates with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        }.ConnectionString;

        await using (var connection = new MySqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Identifier is a test-controlled constant, not user input.
            command.CommandText = $"CREATE DATABASE `{databaseName}`";
            await command.ExecuteNonQueryAsync();
        }

        return new MySqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;
    }

    public override async Task ResetAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = string.Join(
            Environment.NewLine,
            [
                "SET FOREIGN_KEY_CHECKS = 0;",
                .. MappedTables().Select(t => $"TRUNCATE TABLE `{t.Name}`;"),
                "SET FOREIGN_KEY_CHECKS = 1;",
            ]);

        await command.ExecuteNonQueryAsync();
    }

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString)
        => builder.UseMySQL(connectionString);

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
