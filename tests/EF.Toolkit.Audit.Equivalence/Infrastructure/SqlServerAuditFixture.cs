using System.Data.Common;
using System.Runtime.InteropServices;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>SQL Server 2022.</summary>
public sealed class SqlServerAuditFixture : AuditDatabaseFixture
{
    private MsSqlContainer? _container;

    public override string Engine => "sqlserver";

    /// <summary>
    ///     Starts SQL Server, skipping the suite only if the container genuinely will not run.
    /// </summary>
    /// <remarks>
    ///     Microsoft publishes no arm64 image, but the amd64 one runs under Rosetta on Apple
    ///     Silicon — just slowly to start. Skipping on architecture alone would mean developing this
    ///     provider blind.
    /// </remarks>
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
                + (RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? " under arm64 emulation."
                    : ".");
        }
    }

    protected override async Task<string> CreateDatabaseAsync(string databaseName)
    {
        var adminConnectionString = _container!.GetConnectionString();

        await using (var connection = new SqlConnection(adminConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // Identifier is a test-controlled constant, not user input.
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }

    public override async Task ResetAsync()
    {
        var tables = MappedTables()
            .Select(t => $"[{t.Schema ?? "dbo"}].[{t.Name}]")
            .ToList();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        // TRUNCATE is rejected on tables referenced by a foreign key, so constraints come off, rows
        // are deleted, identity is reseeded, and constraints go back on. The reseed branch lands the
        // next value on 1 whether or not the table has ever generated an identity.
        var sql = string.Join(
            Environment.NewLine,
            [
                .. tables.Select(t => $"ALTER TABLE {t} NOCHECK CONSTRAINT ALL;"),
                .. tables.Select(t => $"DELETE FROM {t};"),
                .. tables.Select(t =>
                    $"""
                     IF OBJECTPROPERTY(OBJECT_ID('{t}'), 'TableHasIdentity') = 1
                     BEGIN
                         IF (SELECT last_value FROM sys.identity_columns
                              WHERE object_id = OBJECT_ID('{t}')) IS NULL
                             DBCC CHECKIDENT('{t}', RESEED, 1) WITH NO_INFOMSGS;
                         ELSE
                             DBCC CHECKIDENT('{t}', RESEED, 0) WITH NO_INFOMSGS;
                     END;
                     """),
                .. tables.Select(t => $"ALTER TABLE {t} WITH CHECK CHECK CONSTRAINT ALL;")
            ]);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public override DbConnection OpenConnection()
    {
        var connection = new SqlConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public override string Quote(string identifier) => $"[{identifier}]";

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString,
        bool retryOnFailure)
        => builder.UseSqlServer(
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
        => builder.UseSqlServerAuditing(a => configure(a));

    protected override void ConfigureBulk(DbContextOptionsBuilder<ShopContext> builder)
        => builder.UseSqlServerBulk();

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
