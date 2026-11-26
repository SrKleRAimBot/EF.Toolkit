using System.Data;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>What every test database offers, whatever the engine.</summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
///     Every context a test database creates has <see cref="SqlCaptureInterceptor" /> registered —
///     interceptors are fixed once options are built, so this is the only place it could be done for
///     the test. It records nothing until <see cref="CaptureSql" /> is called.
/// </remarks>
public abstract class TestDatabaseBase<TContext> : IAsyncDisposable, IDisposable
    where TContext : DbContext
{
    private readonly TestDatabaseBuilder<TContext> _settings;
    private readonly Func<DbContextOptions<TContext>, TContext> _factory;
    private readonly SqlCaptureInterceptor _interceptor = new();
    private bool _disposed;

    private protected TestDatabaseBase(
        Action<DbContextOptionsBuilder<TContext>> configureProvider,
        TestDatabaseBuilder<TContext> settings)
    {
        _settings = settings;
        _factory = settings.ContextFactory ?? ContextActivator<TContext>.Factory;

        var builder = new DbContextOptionsBuilder<TContext>();
        configureProvider(builder);
        builder.AddInterceptors(_interceptor);
        foreach (var configure in settings.OptionsCallbacks)
        {
            configure(builder);
        }

        Options = builder.Options;
        Data = new TestData(settings.TestDataSeed);
        TestData.Register(Options, Data);
    }

    /// <summary>The options every context is created with.</summary>
    public DbContextOptions<TContext> Options { get; }

    /// <summary>A test-data builder for this database, seeded from the database's settings.</summary>
    public TestData Data { get; }

    /// <summary>A new context on this database. The caller disposes it.</summary>
    /// <returns>A context with nothing tracked.</returns>
    /// <remarks>
    ///     A fresh context per step of a test — arrange, act, assert — is what keeps each step honest:
    ///     nothing one step tracked can answer a query in the next.
    /// </remarks>
    public TContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _factory(Options);
    }

    /// <summary>Starts recording the SQL of every context this database creates, on the current flow.</summary>
    /// <returns>The capture. Dispose it to stop recording.</returns>
    /// <remarks>
    ///     Commands run by a test executing in parallel are not recorded, even though they go to the
    ///     same database through the same interceptor: a capture follows its own asynchronous flow.
    /// </remarks>
    public SqlCapture CaptureSql() => SqlCapture.ForInterceptor(_interceptor);

    /// <summary>
    ///     Starts a scope whose contexts share one connection and one transaction, rolled back when
    ///     the scope is disposed — so nothing a test writes survives it.
    /// </summary>
    /// <param name="isolationLevel">The transaction's isolation level, or the engine's default.</param>
    /// <param name="cancellationToken">Cancels opening the connection.</param>
    /// <returns>The scope. Create the test's contexts from it, and dispose it at the end of the test.</returns>
    public async Task<IsolatedScope<TContext>> BeginIsolatedScopeAsync(
        IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await IsolatedScope<TContext>.BeginAsync(this, isolationLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Starts a scope whose contexts share one connection and one transaction, rolled back when
    ///     the scope is disposed — so nothing a test writes survives it.
    /// </summary>
    /// <param name="isolationLevel">The transaction's isolation level, or the engine's default.</param>
    /// <returns>The scope. Create the test's contexts from it, and dispose it at the end of the test.</returns>
    public IsolatedScope<TContext> BeginIsolatedScope(IsolationLevel? isolationLevel = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return IsolatedScope<TContext>.Begin(this, isolationLevel);
    }

    /// <summary>Empties every mapped table, then runs the seeders again.</summary>
    /// <param name="cancellationToken">Cancels the reset.</param>
    /// <returns>A task that completes once the database is back to its seeded state.</returns>
    /// <remarks>The reset is configured by <c>ResetWith</c>; see <c>Database.ResetDataAsync</c>.</remarks>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        var context = CreateContext();
        await using (context.ConfigureAwait(false))
        {
            await context.Database.ResetDataAsync(_settings.ResetOptions, cancellationToken).ConfigureAwait(false);
        }

        await SeedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the schema and seeds, as the builder says.</summary>
    private protected async Task InitializeAsync(bool canDelete, CancellationToken cancellationToken)
    {
        var context = CreateContext();
        await using (context.ConfigureAwait(false))
        {
            if (canDelete && _settings.DeleteFirst)
            {
                await context.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
            }

            switch (_settings.CreationStrategy)
            {
                case DatabaseCreationStrategy.EnsureCreated:
                    await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case DatabaseCreationStrategy.Migrate:
                    await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case DatabaseCreationStrategy.None:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown creation strategy {_settings.CreationStrategy}.");
            }
        }

        await SeedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the schema and seeds, as the builder says.</summary>
    private protected void Initialize(bool canDelete)
    {
        using (var context = CreateContext())
        {
            if (canDelete && _settings.DeleteFirst)
            {
                context.Database.EnsureDeleted();
            }

            switch (_settings.CreationStrategy)
            {
                case DatabaseCreationStrategy.EnsureCreated:
                    context.Database.EnsureCreated();
                    break;
                case DatabaseCreationStrategy.Migrate:
                    context.Database.Migrate();
                    break;
                case DatabaseCreationStrategy.None:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown creation strategy {_settings.CreationStrategy}.");
            }
        }

        // Seeders are asynchronous by contract. Blocking here is the price of a synchronous factory,
        // and safe: nothing in a test host's seeding path needs a synchronization context back.
        SeedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var seed in _settings.Seeders)
        {
            var context = CreateContext();
            await using (context.ConfigureAwait(false))
            {
                await seed(context, cancellationToken).ConfigureAwait(false);
                if (context.ChangeTracker.HasChanges())
                {
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Releases the engine-specific resources — the held SQLite connection, for one.</summary>
    /// <returns>A task that completes once they are released.</returns>
    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisposeCoreAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCoreAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}
