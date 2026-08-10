using System.Globalization;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     A database container with two databases — one reached through stock EF Core, one through
///     EF.Toolkit.Bulk — shared by every benchmark class.
/// </summary>
/// <remarks>
///     Benchmarks run against a real engine rather than an in-memory stand-in: the whole subject is
///     the wire protocol, and <c>COPY</c> against a fake would measure nothing.
/// </remarks>
internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly IContainer _container;

    private BenchmarkDatabase(IContainer container, string stock, string bulk)
    {
        _container = container;
        StockConnectionString = stock;
        BulkConnectionString = bulk;
    }

    public static BenchmarkEngine Engine => BenchmarkEngineSelection.Current;

    public string StockConnectionString { get; }
    public string BulkConnectionString { get; }

    public static BenchmarkDatabase Start()
        => Engine == BenchmarkEngine.SqlServer ? StartSqlServer() : StartPostgreSql();

    public BenchmarkContext StockContext() => new(Options(StockConnectionString, bulk: false));

    public BenchmarkContext BulkContext() => new(Options(BulkConnectionString, bulk: true));

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

        using (var stock = new BenchmarkContext(Options(StockConnectionString, bulk: true)))
        {
            stock.BulkInsert(Data.Customers(rows));
        }

        using var bulk = BulkContext();
        bulk.BulkInsert(Data.Customers(rows));
    }

    /// <summary>Fills the bulk database with <paramref name="rows" /> wide customers.</summary>
    public void SeedWide(int rows)
    {
        Reset();

        using var bulk = BulkContext();
        bulk.BulkInsert(Data.WideCustomers(rows));
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private static DbContextOptions<BenchmarkContext> Options(string connectionString, bool bulk)
    {
        var builder = new DbContextOptionsBuilder<BenchmarkContext>();

        if (Engine == BenchmarkEngine.SqlServer)
        {
            builder.UseSqlServer(connectionString);
            if (bulk)
            {
                builder.UseSqlServerBulk();
            }
        }
        else
        {
            builder.UseNpgsql(connectionString);
            if (bulk)
            {
                builder.UseNpgsqlBulk();
            }
        }

        return builder.Options;
    }

    private static BenchmarkDatabase StartPostgreSql()
    {
        var container = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        container.StartAsync().GetAwaiter().GetResult();

        var admin = container.GetConnectionString();

        return Create(
            container,
            name => CreateNpgsqlDatabase(admin, name));
    }

    private static BenchmarkDatabase StartSqlServer()
    {
        var container = new MsSqlBuilder()
            .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        container.StartAsync().GetAwaiter().GetResult();

        var admin = container.GetConnectionString();

        return Create(
            container,
            name => CreateSqlServerDatabase(admin, name));
    }

    private static BenchmarkDatabase Create(IContainer container, Func<string, string> createDatabase)
    {
        var database = new BenchmarkDatabase(
            container,
            createDatabase("bench_stock"),
            createDatabase("bench_bulk"));

        using (var stock = database.StockContext())
        {
            stock.Database.EnsureCreated();
        }

        using var bulk = database.BulkContext();
        bulk.Database.EnsureCreated();

        return database;
    }

    private static string CreateNpgsqlDatabase(string admin, string name)
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

    private static string CreateSqlServerDatabase(string admin, string name)
    {
        using (var connection = new SqlConnection(admin))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]";
            command.ExecuteNonQuery();
        }

        return new SqlConnectionStringBuilder(admin) { InitialCatalog = name }.ConnectionString;
    }

    private static void Truncate(string connectionString)
    {
        if (Engine == BenchmarkEngine.SqlServer)
        {
            TruncateSqlServer(connectionString);
            return;
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "TRUNCATE \"Customers\", \"WideCustomers\" RESTART IDENTITY CASCADE";
        command.ExecuteNonQuery();
    }

    private static void TruncateSqlServer(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();

        foreach (var table in new[] { "Customers", "WideCustomers" })
        {
            using var command = connection.CreateCommand();

            // TRUNCATE resets the identity seed on its own, which is what keeps generated keys
            // identical between the two databases from one iteration to the next.
            command.CommandText = string.Create(
                CultureInfo.InvariantCulture, $"TRUNCATE TABLE [{table}]");
            command.ExecuteNonQuery();
        }
    }
}
