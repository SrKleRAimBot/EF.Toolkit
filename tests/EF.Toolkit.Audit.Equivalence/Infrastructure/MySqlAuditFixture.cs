using System.Data.Common;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Testcontainers.MySql;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>MySQL 8.4, through Oracle's MySql.EntityFrameworkCore provider.</summary>
/// <remarks>
///     Run with <c>innodb_autoinc_lock_mode = 1</c>, the configuration in which a bulk insert's
///     generated keys come back from one multi-row statement — the path the bulk-audit scenarios
///     are meant to exercise.
/// </remarks>
public sealed class MySqlAuditFixture : AuditDatabaseFixture
{
    private MySqlContainer? _container;

    public override string Engine => "mysql";

    /// <inheritdoc />
    /// <remarks>The MySQL provider package keeps the table in the context's own database.</remarks>
    public override string AuditTable => Quote(AuditOptions.DefaultTableName);

    protected override async Task StartContainerAsync()
    {
        _container = new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword("mysql")
            .WithCommand("--innodb-autoinc-lock-mode=1")
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
            Database = databaseName
        }.ConnectionString;
    }

    public override async Task ResetAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // TRUNCATE resets AUTO_INCREMENT. Foreign key checks come off for the session so the tables
        // can be emptied in any order.
        command.CommandText = string.Join(
            Environment.NewLine,
            [
                "SET FOREIGN_KEY_CHECKS = 0;",
                .. MappedTables().Select(t => $"TRUNCATE TABLE `{t.Name}`;"),
                "SET FOREIGN_KEY_CHECKS = 1;",
            ]);

        await command.ExecuteNonQueryAsync();
    }

    public override DbConnection OpenConnection()
    {
        var connection = new MySqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public override string Quote(string identifier) => $"`{identifier}`";

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

    protected override void ConfigureAuditing(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<AuditOptionsBuilder> configure)
        => builder.UseMySqlAuditing(a => configure(a));

    protected override void ConfigureBulk(DbContextOptionsBuilder<ShopContext> builder)
        => builder.UseMySqlBulk();

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
