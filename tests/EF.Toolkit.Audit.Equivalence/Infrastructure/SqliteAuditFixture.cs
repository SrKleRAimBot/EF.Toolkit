using System.Data.Common;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Equivalence.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>SQLite, in a database file of its own — no container required.</summary>
/// <remarks>
///     A file rather than an in-memory database, because scenarios read from connections other than
///     the one that wrote. Write-ahead logging lets those readers see the last committed state while
///     a writer holds its transaction open, the way the other engines' readers do.
/// </remarks>
public sealed class SqliteAuditFixture : AuditDatabaseFixture
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"efaudit-sqlite-{Guid.NewGuid():N}");

    public override string Engine => "sqlite";

    public override bool SupportsAmbientTransactions => false;

    public override bool SupportsRetryingExecutionStrategy => false;

    /// <inheritdoc />
    /// <remarks>SQLite has no schemas.</remarks>
    public override string AuditTable => Quote(AuditOptions.DefaultTableName);

    protected override Task StartContainerAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    protected override async Task<string> CreateDatabaseAsync(string databaseName)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, databaseName + ".db"),
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // Persistent: the journal mode is a property of the file, not of the connection.
        command.CommandText = "PRAGMA journal_mode = WAL";
        await command.ExecuteNonQueryAsync();

        return connectionString;
    }

    public override async Task ResetAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // Foreign keys come off so the tables can be emptied in any order, and back on before the
        // connection returns to the pool: the pragma belongs to the connection, and a pooled one
        // left with it off would silently stop enforcing them for whichever scenario opened it next.
        command.CommandText = string.Join(
            Environment.NewLine,
            [
                "PRAGMA foreign_keys = OFF;",
                .. MappedTables().Select(t => $"DELETE FROM \"{t.Name}\";"),
                "DELETE FROM sqlite_sequence;",
                "PRAGMA foreign_keys = ON;",
            ]);

        await command.ExecuteNonQueryAsync();
    }

    public override DbConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    public override string Quote(string identifier) => $"\"{identifier}\"";

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString,
        bool retryOnFailure)
        // SQLite has no transient-failure retry strategy to configure, so retryOnFailure has
        // nothing to switch on here.
        => builder.UseSqlite(connectionString);

    protected override void ConfigureAuditing(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<AuditOptionsBuilder> configure)
        => builder.UseSqliteAuditing(a => configure(a));

    protected override void ConfigureBulk(DbContextOptionsBuilder<ShopContext> builder)
        => builder.UseSqliteBulk();

    public override ValueTask DisposeAsync()
    {
        // Pooled connections hold the file open, and Windows will not delete an open file.
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is harmless; failing the run over it is not.
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
