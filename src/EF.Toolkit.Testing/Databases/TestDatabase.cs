using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>A test database on a real engine — PostgreSQL, SQL Server, MySQL, or SQLite on disk.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
///     Provider-neutral: the provider is whatever the configure delegate applies. Starting the engine
///     is left to the test — typically a Testcontainers container in a fixture — so this package
///     depends on neither.
/// </remarks>
/// <example>
///     <code>
///     await using var db = await TestDatabase&lt;AppDb&gt;.CreateAsync(
///         o => o.UseNpgsql(container.GetConnectionString()),
///         o => o.UseMigrations().DeleteExisting());
///     </code>
/// </example>
// CA1000: the factories are the type's whole construction surface, and the caller names the context
// type either way — SqliteTestDatabase<AppDb>.CreateAsync() — so there is no inference to lose.
#pragma warning disable CA1000
public sealed class TestDatabase<TContext> : TestDatabaseBase<TContext>
    where TContext : DbContext
{
    private TestDatabase(
        Action<DbContextOptionsBuilder<TContext>> configureProvider,
        TestDatabaseBuilder<TContext> settings)
        : base(configureProvider, settings)
    {
    }

    /// <summary>Creates the schema and seeds the database.</summary>
    /// <param name="configureProvider">Applies the provider: <c>o => o.UseNpgsql(connectionString)</c>.</param>
    /// <param name="configure">Extra options, seeders, the creation strategy and the context factory.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The database. Disposing it leaves the database in place.</returns>
    public static async Task<TestDatabase<TContext>> CreateAsync(
        Action<DbContextOptionsBuilder<TContext>> configureProvider,
        Action<TestDatabaseBuilder<TContext>>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configureProvider);

        var database = new TestDatabase<TContext>(configureProvider, TestDatabaseBuilder<TContext>.Build(configure));
        await database.InitializeAsync(canDelete: true, cancellationToken).ConfigureAwait(false);
        return database;
    }

    /// <summary>Creates the schema and seeds the database.</summary>
    /// <param name="configureProvider">Applies the provider: <c>o => o.UseNpgsql(connectionString)</c>.</param>
    /// <param name="configure">Extra options, seeders, the creation strategy and the context factory.</param>
    /// <returns>The database. Disposing it leaves the database in place.</returns>
    public static TestDatabase<TContext> Create(
        Action<DbContextOptionsBuilder<TContext>> configureProvider,
        Action<TestDatabaseBuilder<TContext>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configureProvider);

        var database = new TestDatabase<TContext>(configureProvider, TestDatabaseBuilder<TContext>.Build(configure));
        database.Initialize(canDelete: true);
        return database;
    }
}
#pragma warning restore CA1000
