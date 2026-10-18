using EFToolkit.Query.Equivalence.Model;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Equivalence.Infrastructure;

/// <summary>SQLite, in a database file of its own — no container required.</summary>
public sealed class SqliteQueryFixture : QueryDatabaseFixture
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"efquery-sqlite-{Guid.NewGuid():N}");

    public override string Engine => "sqlite";

    public override bool OrdersByDateTimeOffset => false;

    protected override Task StartContainerAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    protected override Task<string> CreateDatabaseAsync(string databaseName)
        => Task.FromResult(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, databaseName + ".db"),
        }.ConnectionString);

    public override async Task ResetAsync()
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        // Foreign keys come off so the tables can be emptied in any order, and back on before the
        // connection returns to the pool: the pragma belongs to the connection.
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
        string connectionString)
        => builder.UseSqlite(connectionString);

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
