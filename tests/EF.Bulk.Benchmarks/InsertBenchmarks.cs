using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace EFBulk.Benchmarks;

/// <summary>
///     Insert throughput on PostgreSQL: stock EF Core, transparent EF.Bulk, and the explicit API.
/// </summary>
/// <remarks>
///     <para>
///         The three are measured together because the interesting result is the gap between them.
///         Transparent mode replaces only how a batch is executed, so it is bounded by EF's own
///         change detection and command materialisation; the explicit API skips that pipeline
///         entirely and is where the large numbers come from.
///     </para>
///     <para>
///         Runs against a real PostgreSQL in Docker rather than an in-memory stand-in. The whole
///         point is the wire protocol — <c>COPY</c> against a fake would measure nothing.
///     </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 5)]
public class InsertBenchmarks
{
    private PostgreSqlContainer _container = null!;
    private string _stockConnectionString = null!;
    private string _bulkConnectionString = null!;
    private List<Customer> _customers = null!;

    [Params(1_000, 10_000, 100_000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        _container.StartAsync().GetAwaiter().GetResult();

        _stockConnectionString = CreateDatabase("bench_stock");
        _bulkConnectionString = CreateDatabase("bench_bulk");

        using (var stock = StockContext())
        {
            stock.Database.EnsureCreated();
        }

        using var bulk = BulkContext();
        bulk.Database.EnsureCreated();
    }

    [GlobalCleanup]
    public void Cleanup() => _container.DisposeAsync().AsTask().GetAwaiter().GetResult();

    // Rebuilding the entities each iteration matters: an insert writes generated keys back onto
    // them, and a second run over the same objects would no longer be inserting fresh rows.
    [IterationSetup]
    public void IterationSetup()
    {
        _customers = Data.Customers(Rows);
        Truncate(_stockConnectionString);
        Truncate(_bulkConnectionString);
    }

    [Benchmark(Baseline = true, Description = "SaveChanges (stock EF)")]
    public async Task StockSaveChanges()
    {
        await using var context = StockContext();
        context.Customers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "SaveChanges (EF.Bulk)")]
    public async Task TransparentSaveChanges()
    {
        await using var context = BulkContext();
        context.Customers.AddRange(_customers);
        await context.SaveChangesAsync();
    }

    [Benchmark(Description = "BulkInsertAsync")]
    public async Task ExplicitBulkInsert()
    {
        await using var context = BulkContext();
        await context.BulkInsertAsync(_customers);
    }

    private BenchmarkContext StockContext()
    {
        var builder = new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNpgsql(_stockConnectionString);

        return new BenchmarkContext(builder.Options);
    }

    private BenchmarkContext BulkContext()
    {
        var builder = new DbContextOptionsBuilder<BenchmarkContext>()
            .UseNpgsql(_bulkConnectionString)
            .UseNpgsqlBulk();

        return new BenchmarkContext(builder.Options);
    }

    private string CreateDatabase(string name)
    {
        var admin = _container.GetConnectionString();

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
