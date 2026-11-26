using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>An in-memory SQLite database that lives exactly as long as this object.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
///     <para>
///         An in-memory SQLite database exists for as long as a connection to it is open, and each new
///         connection to <c>:memory:</c> is a different, empty database. So the connection is opened
///         once, held open until disposal, and handed to every context — which is what makes data
///         written through one context visible to the next.
///     </para>
///     <para>
///         A real relational engine, unlike EF's in-memory provider: foreign keys, unique indexes,
///         transactions and SQL translation all behave as they do on disk. It is not the production
///         engine, though; see the docs for what differs.
///     </para>
///     <para>
///         The shared connection is not thread-safe. Contexts from one database must not run
///         commands concurrently — give each parallel test its own database, which costs about a
///         millisecond.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     await using var db = await SqliteTestDatabase&lt;AppDb&gt;.CreateAsync(o => o
///         .ConfigureOptions(b => b.UseBulkOperations())
///         .Seed(ctx => ctx.Countries.Add(new Country("NZ"))));
///
///     await using var context = db.CreateContext();
///     </code>
/// </example>
// CA1000: the factories are the type's whole construction surface, and the caller names the context
// type either way — SqliteTestDatabase<AppDb>.CreateAsync() — so there is no inference to lose.
#pragma warning disable CA1000
public sealed class SqliteTestDatabase<TContext> : TestDatabaseBase<TContext>
    where TContext : DbContext
{
    private readonly SqliteConnection _connection;

    private SqliteTestDatabase(SqliteConnection connection, TestDatabaseBuilder<TContext> settings)
        : base(b => b.UseSqlite(connection), settings)
    {
        _connection = connection;
    }

    /// <summary>The connection held open for the database's lifetime.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>Creates the database, its schema and its seed data.</summary>
    /// <param name="configure">Extra options, seeders, the creation strategy and the context factory.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The database. Dispose it to release it.</returns>
    public static async Task<SqliteTestDatabase<TContext>> CreateAsync(
        Action<TestDatabaseBuilder<TContext>>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        SqliteTestDatabase<TContext>? database = null;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            database = new SqliteTestDatabase<TContext>(connection, TestDatabaseBuilder<TContext>.Build(configure));
            await database.InitializeAsync(canDelete: false, cancellationToken).ConfigureAwait(false);
            return database;
        }
        catch
        {
            if (database is not null)
            {
                await database.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Creates the database, its schema and its seed data.</summary>
    /// <param name="configure">Extra options, seeders, the creation strategy and the context factory.</param>
    /// <returns>The database. Dispose it to release it.</returns>
    public static SqliteTestDatabase<TContext> Create(Action<TestDatabaseBuilder<TContext>>? configure = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        SqliteTestDatabase<TContext>? database = null;
        try
        {
            connection.Open();
            database = new SqliteTestDatabase<TContext>(connection, TestDatabaseBuilder<TContext>.Build(configure));
            database.Initialize(canDelete: false);
            return database;
        }
        catch
        {
            if (database is not null)
            {
                database.Dispose();
            }
            else
            {
                connection.Dispose();
            }

            throw;
        }
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeCoreAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        await base.DisposeCoreAsync().ConfigureAwait(false);
    }
}
#pragma warning restore CA1000
