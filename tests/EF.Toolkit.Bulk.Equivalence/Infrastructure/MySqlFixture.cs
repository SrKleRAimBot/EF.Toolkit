using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace EFToolkit.Bulk.Equivalence.Infrastructure;

/// <summary>MySQL 8.4, through Oracle's MySql.EntityFrameworkCore provider.</summary>
/// <remarks>
///     The server runs with <c>innodb_autoinc_lock_mode = 1</c>. Under MySQL 8's default of 2 a
///     multi-row insert's AUTO_INCREMENT values carry no consecutiveness guarantee, so every insert
///     that needs its keys back declines to stock EF — correct, and exactly what the suite would
///     then be unable to tell apart from a regression. Lock mode 1 is the configuration in which
///     the fast path exists, so it is the one the fast path is tested under.
/// </remarks>
public class MySqlFixture : DatabaseFixture
{
    private MySqlContainer? _container;

    public override string Engine => "mysql";

    /// <summary>The InnoDB AUTO_INCREMENT lock mode the server runs with.</summary>
    protected virtual int AutoIncrementLockMode => 1;

    protected override async Task StartContainerAsync()
    {
        _container = new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword("mysql")
            .WithCommand($"--innodb-autoinc-lock-mode={AutoIncrementLockMode}")
            .Build();

        await _container.StartAsync();
    }

    protected override async Task<string> CreateDatabaseAsync(string databaseName)
    {
        var adminConnectionString = AdminConnectionString();

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
            Database = databaseName
        }.ConnectionString;
    }

    protected override async Task ResetAsync(string connectionString)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // TRUNCATE resets AUTO_INCREMENT, which the harness compares. Foreign key checks come off
        // for the session so the tables can be emptied in any order.
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
        string connectionString,
        bool retryOnFailure)
        => builder.UseMySQL(
            connectionString,
            o =>
            {
                if (retryOnFailure)
                {
                    o.EnableRetryOnFailure();
                }
            });

    protected override void ConfigureBulk(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<BulkOptionsBuilder>? configure)
        => builder.UseMySqlBulk(b => configure?.Invoke(b));

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private string AdminConnectionString()
        => new MySqlConnectionStringBuilder(_container!.GetConnectionString())
        {
            // The container's root account authenticates with caching_sha2_password.
            AllowPublicKeyRetrieval = true,
        }.ConnectionString;
}

/// <summary>
///     MySQL 8.4 as it ships, with <c>innodb_autoinc_lock_mode = 2</c>.
/// </summary>
/// <remarks>
///     Under this mode a multi-row insert's generated keys carry no guarantee of being consecutive,
///     so every insert that needs its keys back has to decline. Running the differential suites here
///     is what proves the decline happens before anything is written: a fallback that replayed rows
///     already inserted would show up as duplicated data.
/// </remarks>
public sealed class MySqlInterleavedFixture : MySqlFixture
{
    protected override int AutoIncrementLockMode => 2;
}
