using System.Runtime.InteropServices;
using EFBulk.Configuration;
using EFBulk.Equivalence.Model;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace EFBulk.Equivalence.Infrastructure;

/// <summary>SQL Server 2022.</summary>
public sealed class SqlServerFixture : DatabaseFixture
{
    private MsSqlContainer? _container;

    public override string Engine => "sqlserver";

    /// <summary>
    ///     Starts SQL Server, skipping the suite only if the container genuinely will not run.
    /// </summary>
    /// <remarks>
    ///     Microsoft publishes no arm64 image, but the amd64 one runs perfectly well under Rosetta
    ///     on Apple Silicon — just slowly to start. Skipping on architecture alone would mean
    ///     developing this provider entirely blind, so the fixture attempts the container and only
    ///     gives up if starting it actually fails.
    /// </remarks>
    protected override async Task StartContainerAsync()
    {
        _container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        // Emulated startup takes well over a minute on Apple Silicon, against a few seconds on a
        // native amd64 CI runner.
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

    protected override async Task ResetAsync(string connectionString)
    {
        var tables = MappedTables()
            .Select(t => $"[{t.Schema ?? "dbo"}].[{t.Name}]")
            .ToList();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // TRUNCATE is rejected on tables referenced by a foreign key, so constraints come off,
        // rows are deleted, identity is reseeded, and constraints go back on with a full recheck.
        //
        // The reseed value is history-dependent, and getting it wrong is subtle enough to be worth
        // spelling out: DBCC CHECKIDENT(RESEED, n) makes the next inserted value n if the table has
        // never generated an identity, but n + 1 if it has. sys.identity_columns.last_value is NULL
        // in exactly the first case, so branching on it lands the next value on 1 either way.
        // Without this, two databases whose tables have different insert histories — which happens
        // as soon as one test writes to only one of them — start generating different keys.
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

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString)
        => builder.UseSqlServer(connectionString);

    protected override void ConfigureBulk(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<BulkOptionsBuilder>? configure)
        => builder.UseSqlServerBulk(b => configure?.Invoke(b));

    public override async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
