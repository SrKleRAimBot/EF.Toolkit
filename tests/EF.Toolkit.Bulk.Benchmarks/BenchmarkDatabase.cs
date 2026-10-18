using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

namespace EFToolkit.Bulk.Benchmarks;

/// <summary>
///     A database container with two databases — one reached through stock EF Core, one through
///     EF.Toolkit.Bulk — shared by every benchmark class.
/// </summary>
/// <remarks>
///     Benchmarks run against a real engine rather than an in-memory stand-in: the whole subject is
///     the wire protocol, and <c>COPY</c> against a fake would measure nothing. SQLite is the
///     exception that proves it — it is in-process, so there is no container, just two files.
/// </remarks>
internal sealed class BenchmarkDatabase : IAsyncDisposable
{
    private readonly IAsyncDisposable _resource;

    private BenchmarkDatabase(IAsyncDisposable resource, string stock, string bulk)
    {
        _resource = resource;
        StockConnectionString = stock;
        BulkConnectionString = bulk;
    }

    public static BenchmarkEngine Engine => BenchmarkEngineSelection.Current;

    public string StockConnectionString { get; }
    public string BulkConnectionString { get; }

    public static BenchmarkDatabase Start()
        => Engine switch
        {
            BenchmarkEngine.SqlServer => StartSqlServer(),
            BenchmarkEngine.MySql => StartMySql(),
            BenchmarkEngine.Sqlite => StartSqlite(),
            _ => StartPostgreSql(),
        };

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

    public async ValueTask DisposeAsync() => await _resource.DisposeAsync();

    private static DbContextOptions<BenchmarkContext> Options(string connectionString, bool bulk)
    {
        var builder = new DbContextOptionsBuilder<BenchmarkContext>();

        switch (Engine)
        {
            case BenchmarkEngine.SqlServer:
                builder.UseSqlServer(connectionString);
                if (bulk)
                {
                    builder.UseSqlServerBulk();
                }

                break;

            case BenchmarkEngine.MySql:
                builder.UseMySQL(connectionString);
                if (bulk)
                {
                    builder.UseMySqlBulk();
                }

                break;

            case BenchmarkEngine.Sqlite:
                builder.UseSqlite(connectionString);
                if (bulk)
                {
                    builder.UseSqliteBulk();
                }

                break;

            default:
                builder.UseNpgsql(connectionString);
                if (bulk)
                {
                    builder.UseNpgsqlBulk();
                }

                break;
        }

        return builder.Options;
    }

    private static BenchmarkDatabase StartPostgreSql()
    {
        var container = new PostgreSqlBuilder("postgres:16-alpine")
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
        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        container.StartAsync().GetAwaiter().GetResult();

        var admin = container.GetConnectionString();

        return Create(
            container,
            name => CreateSqlServerDatabase(admin, name));
    }

    private static BenchmarkDatabase StartMySql()
    {
        // Lock mode 1, under which a multi-row insert's generated keys are consecutive and come back
        // from one statement. MySQL 8's default of 2 inserts row by row to learn them instead, which
        // is the configuration worth benchmarking separately rather than by accident.
        var container = new MySqlBuilder("mysql:8.4")
            .WithUsername("root")
            .WithPassword("mysql")
            .WithCommand("--innodb-autoinc-lock-mode=1")
            .Build();

        container.StartAsync().GetAwaiter().GetResult();

        var admin = new MySqlConnectionStringBuilder(container.GetConnectionString())
        {
            AllowPublicKeyRetrieval = true,
        }.ConnectionString;

        return Create(container, name => CreateMySqlDatabase(admin, name));
    }

    private static BenchmarkDatabase StartSqlite()
    {
        var directory = new TemporaryDirectory();

        return Create(
            directory,
            name => new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(directory.Path, name + ".db"),
            }.ConnectionString);
    }

    private static BenchmarkDatabase Create(IAsyncDisposable container, Func<string, string> createDatabase)
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

    private static string CreateMySqlDatabase(string admin, string name)
    {
        using (var connection = new MySqlConnection(admin))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE `{name}`";
            command.ExecuteNonQuery();
        }

        return new MySqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }

    private static void Truncate(string connectionString)
    {
        switch (Engine)
        {
            case BenchmarkEngine.SqlServer:
                TruncateSqlServer(connectionString);
                return;

            case BenchmarkEngine.MySql:
                TruncateMySql(connectionString);
                return;

            case BenchmarkEngine.Sqlite:
                TruncateSqlite(connectionString);
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

    private static void TruncateMySql(string connectionString)
    {
        using var connection = new MySqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();

        // TRUNCATE resets AUTO_INCREMENT, keeping generated keys identical between the databases.
        command.CommandText = "TRUNCATE TABLE `Customers`; TRUNCATE TABLE `WideCustomers`;";
        command.ExecuteNonQuery();
    }

    private static void TruncateSqlite(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();

        // sqlite_sequence is the AUTOINCREMENT high-water mark, so clearing it resets the keys.
        command.CommandText =
            "DELETE FROM \"Customers\"; DELETE FROM \"WideCustomers\"; DELETE FROM sqlite_sequence;";
        command.ExecuteNonQuery();
    }

    /// <summary>A directory of database files, deleted with the benchmark run.</summary>
    private sealed class TemporaryDirectory : IAsyncDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"efbulk-bench-{Guid.NewGuid():N}")).FullName;

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}
