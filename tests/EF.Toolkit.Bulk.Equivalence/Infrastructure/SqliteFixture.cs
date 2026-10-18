using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Equivalence.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Bulk.Equivalence.Infrastructure;

/// <summary>SQLite, in database files of its own — no container required.</summary>
/// <remarks>
///     Files rather than in-memory databases, because the harness reads from connections other than
///     the one that wrote: an in-memory database belongs to a single connection, and a shared-cache
///     one changes the locking the tests are trying to observe. Write-ahead logging lets those
///     readers see the last committed state while a writer holds its transaction open, the way the
///     other engines' readers do.
/// </remarks>
public sealed class SqliteFixture : DatabaseFixture
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"efbulk-sqlite-{Guid.NewGuid():N}");

    public override string Engine => "sqlite";

    public override bool ReadsBackGeneratedNonKeyColumns => true;

    public override bool SupportsAmbientTransactions => false;

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

    protected override async Task ResetAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // sqlite_sequence holds each AUTOINCREMENT table's high-water mark, which is the key
        // generator the harness compares. Foreign keys come off so the tables can be emptied in any
        // order, and back on before the connection returns to the pool: the pragma belongs to the
        // connection, and a pooled one left with it off would silently stop enforcing them for
        // whichever test opened it next.
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

    protected override void ConfigureProvider(
        DbContextOptionsBuilder<ShopContext> builder,
        string connectionString,
        bool retryOnFailure)
        // SQLite has no transient-failure retry strategy to configure, so retryOnFailure has
        // nothing to switch on here.
        => builder.UseSqlite(connectionString);

    protected override void ConfigureBulk(
        DbContextOptionsBuilder<ShopContext> builder,
        Action<BulkOptionsBuilder>? configure)
        => builder.UseSqliteBulk(b => configure?.Invoke(b));

    public override ValueTask DisposeAsync()
    {
        // Pooled connections hold the files open, and Windows will not delete an open file.
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
