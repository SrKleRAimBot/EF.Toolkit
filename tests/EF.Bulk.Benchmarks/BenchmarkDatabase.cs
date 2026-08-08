using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EFBulk.Benchmarks;

/// <summary>
///     A PostgreSQL container with two databases — one reached through stock EF Core, one through
///     EF.Bulk — shared by every benchmark class.
/// </summary>
/// <remarks>
///     Benchmarks run against a real engine rather than an in-memory stand-in: the whole subject is
///     the wire protocol, and <c>COPY</c> against a fake would measure nothing.
/// </remarks>
internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;

    private BenchmarkDatabase(PostgreSqlContainer container, string stock, string bulk)
    {
        _container = container;
        StockConnectionString = stock;
        BulkConnectionString = bulk;
    }

    public string StockConnectionString { get; }
    public string BulkConnectionString { get; }

    public static BenchmarkDatabase Start()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        container.StartAsync().GetAwaiter().GetResult();

        var admin = container.GetConnectionString();
        var database = new BenchmarkDatabase(
            container,
            CreateDatabase(admin, "bench_stock"),
            CreateDatabase(admin, "bench_bulk"));

        using (var stock = database.StockContext())
        {
            stock.Database.EnsureCreated();
        }

        using var bulk = database.BulkContext();
        bulk.Database.EnsureCreated();

        return database;
    }

    public BenchmarkContext StockContext()
        => new(new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNpgsql(StockConnectionString)
            .Options);

    public BenchmarkContext BulkContext()
        => new(new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNpgsql(BulkConnectionString)
            .UseNpgsqlBulk()
            .Options);

    /// <summary>Empties both databases and resets their identity sequences.</summary>
    public void Reset()
    {
        Truncate(StockConnectionString);
        Truncate(BulkConnectionString);
    }

    /// <summary>
    ///     Fills both databases with <paramref name="rows" /> customers, through the fast path.
    /// </summary>
    /// <remarks>
    ///     Seeding is setup, not measurement, so it uses the bulk path on both sides to keep
    ///     iteration overhead down. Both databases end up with identical contents and identical
    ///     keys, since sequences are reset first.
    /// </remarks>
    public void Seed(int rows)
    {
        Reset();

        using (var stock = BulkStockContext())
        {
            stock.BulkInsert(Data.Customers(rows));
        }

        using var bulk = BulkContext();
        bulk.BulkInsert(Data.Customers(rows));
    }

    /// <summary>The stock database, reached through EF.Bulk purely so seeding is fast.</summary>
    private BenchmarkContext BulkStockContext()
        => new(new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNpgsql(StockConnectionString)
            .UseNpgsqlBulk()
            .Options);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static string CreateDatabase(string admin, string name)
    {
        using (var connection = new NpgsqlConnection(admin))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            command.ExecuteNonQuery();
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    private static void Truncate(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE \"Customers\" RESTART IDENTITY CASCADE";
        command.ExecuteNonQuery();
    }
}
