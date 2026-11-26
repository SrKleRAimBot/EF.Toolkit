using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>Settings shared by <see cref="SqliteTestDatabase{TContext}" /> and <see cref="TestDatabase{TContext}" />.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
public sealed class TestDatabaseBuilder<TContext>
    where TContext : DbContext
{
    private readonly List<Action<DbContextOptionsBuilder<TContext>>> _configureOptions = [];
    private readonly List<Func<TContext, CancellationToken, Task>> _seeders = [];

    internal TestDatabaseBuilder()
    {
    }

    internal IReadOnlyList<Action<DbContextOptionsBuilder<TContext>>> OptionsCallbacks => _configureOptions;

    internal IReadOnlyList<Func<TContext, CancellationToken, Task>> Seeders => _seeders;

    internal Func<DbContextOptions<TContext>, TContext>? ContextFactory { get; private set; }

    internal DatabaseCreationStrategy CreationStrategy { get; private set; } = DatabaseCreationStrategy.EnsureCreated;

    internal bool DeleteFirst { get; private set; }

    internal Action<ResetDataOptions>? ResetOptions { get; private set; }

    internal int TestDataSeed { get; private set; }

    /// <summary>Adds options after the provider — <c>UseBulkOperations()</c>, logging, warnings.</summary>
    /// <param name="configure">Applied to every context the database creates. May be called more than once.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> ConfigureOptions(Action<DbContextOptionsBuilder<TContext>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _configureOptions.Add(configure);
        return this;
    }

    /// <summary>Constructs contexts with <paramref name="factory" /> rather than by reflection.</summary>
    /// <param name="factory">
    ///     Builds a context from the database's options. Needed when the context's constructor takes
    ///     more than the options.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> UseContextFactory(Func<DbContextOptions<TContext>, TContext> factory)
    {
        ContextFactory = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    /// <summary>Creates the schema with <paramref name="strategy" />. The default is <c>EnsureCreated</c>.</summary>
    /// <param name="strategy">How the schema is created.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> CreateWith(DatabaseCreationStrategy strategy)
    {
        CreationStrategy = strategy;
        return this;
    }

    /// <summary>Creates the schema by running the application's migrations.</summary>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> UseMigrations() => CreateWith(DatabaseCreationStrategy.Migrate);

    /// <summary>Drops the database before creating it, so a schema left by an earlier run is replaced.</summary>
    /// <param name="delete">Whether to drop it.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>Has no effect on an in-memory SQLite database, which always starts empty.</remarks>
    public TestDatabaseBuilder<TContext> DeleteExisting(bool delete = true)
    {
        DeleteFirst = delete;
        return this;
    }

    /// <summary>Seeds data once, when the database is created, and again after each <c>ResetAsync</c>.</summary>
    /// <param name="seeder">The seeder. Seeders run in registration order, each on a fresh context.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> Seed(ISeeder<TContext> seeder)
    {
        ArgumentNullException.ThrowIfNull(seeder);
        _seeders.Add(seeder.SeedAsync);
        return this;
    }

    /// <summary>Seeds data once, when the database is created, and again after each <c>ResetAsync</c>.</summary>
    /// <param name="seed">Adds the data. Anything left unsaved is saved afterwards.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> Seed(Func<TContext, CancellationToken, Task> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _seeders.Add(seed);
        return this;
    }

    /// <summary>Seeds data once, when the database is created, and again after each <c>ResetAsync</c>.</summary>
    /// <param name="seed">Adds the data. Anything left unsaved is saved afterwards.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     o.Seed(db => db.Countries.AddRange(new Country("NZ"), new Country("AU")));
    ///     </code>
    /// </example>
    public TestDatabaseBuilder<TContext> Seed(Action<TContext> seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        _seeders.Add((context, _) =>
        {
            seed(context);
            return Task.CompletedTask;
        });
        return this;
    }

    /// <summary>Configures the reset <c>ResetAsync</c> performs.</summary>
    /// <param name="configure">Tables to keep, and whether identities restart.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> ResetWith(Action<ResetDataOptions> configure)
    {
        ResetOptions = configure ?? throw new ArgumentNullException(nameof(configure));
        return this;
    }

    /// <summary>Seeds the database's <see cref="TestDatabaseBase{TContext}.Data" /> builder. Default 0.</summary>
    /// <param name="seed">The seed. The same seed and the same calls build the same values.</param>
    /// <returns>This builder, for chaining.</returns>
    public TestDatabaseBuilder<TContext> WithTestDataSeed(int seed)
    {
        TestDataSeed = seed;
        return this;
    }

    internal static TestDatabaseBuilder<TContext> Build(Action<TestDatabaseBuilder<TContext>>? configure)
    {
        var builder = new TestDatabaseBuilder<TContext>();
        configure?.Invoke(builder);
        return builder;
    }
}
