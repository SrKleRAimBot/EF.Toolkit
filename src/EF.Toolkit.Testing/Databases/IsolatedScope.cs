using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Testing;

/// <summary>
///     Contexts sharing one connection and one transaction, which is rolled back on dispose: the
///     classic "each test in a transaction" isolation.
/// </summary>
/// <typeparam name="TContext">The context type.</typeparam>
/// <remarks>
///     <para>
///         One root context owns the connection and the transaction for the scope's lifetime. Every
///         context from <see cref="CreateContext" /> is pointed at that connection before it opens
///         its own and enlisted in the transaction, so they all see each other's uncommitted writes
///         — which is what lets an arrange context, an act context and an assert context cooperate
///         inside one test.
///     </para>
///     <para>
///         <c>SaveChanges</c> inside the scope joins the scope's transaction rather than starting one
///         of its own. Code under test that calls <c>Database.BeginTransaction</c> itself is refused by
///         EF, since a context can hold only one transaction; for that code, create savepoints on
///         <c>Database.CurrentTransaction</c> instead (<c>CreateSavepoint</c> /
///         <c>RollbackToSavepoint</c>), or reset the database between tests rather than isolating.
///     </para>
/// </remarks>
public sealed class IsolatedScope<TContext> : IAsyncDisposable, IDisposable
    where TContext : DbContext
{
    private readonly TestDatabaseBase<TContext> _database;
    private readonly TContext _root;
    private readonly IDbContextTransaction _transaction;
    private readonly List<TContext> _contexts = [];
    private bool _disposed;

    private IsolatedScope(TestDatabaseBase<TContext> database, TContext root, IDbContextTransaction transaction)
    {
        _database = database;
        _root = root;
        _transaction = transaction;
    }

    /// <summary>The connection every context in the scope uses.</summary>
    public DbConnection Connection => _root.Database.GetDbConnection();

    /// <summary>The transaction every context in the scope is enlisted in.</summary>
    public DbTransaction Transaction => _transaction.GetDbTransaction();

    internal static async Task<IsolatedScope<TContext>> BeginAsync(
        TestDatabaseBase<TContext> database,
        IsolationLevel? isolationLevel,
        CancellationToken cancellationToken)
    {
        var root = database.CreateContext();
        try
        {
            await root.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var transaction = isolationLevel is { } level
                ? await root.Database.BeginTransactionAsync(level, cancellationToken).ConfigureAwait(false)
                : await root.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new IsolatedScope<TContext>(database, root, transaction);
        }
        catch
        {
            await root.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IsolatedScope<TContext> Begin(TestDatabaseBase<TContext> database, IsolationLevel? isolationLevel)
    {
        var root = database.CreateContext();
        try
        {
            root.Database.OpenConnection();
            var transaction = isolationLevel is { } level
                ? root.Database.BeginTransaction(level)
                : root.Database.BeginTransaction();
            return new IsolatedScope<TContext>(database, root, transaction);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    /// <summary>A new context on the scope's connection, enlisted in its transaction.</summary>
    /// <returns>
    ///     The context. Disposing it is optional — the scope disposes whatever is left — but it does
    ///     not close the shared connection.
    /// </returns>
    public TContext CreateContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = _database.CreateContext();
        try
        {
            // Before the context has opened anything of its own: a connection can only be swapped
            // while closed. The scope owns it, so the context must not dispose it.
            context.Database.SetDbConnection(Connection, contextOwnsConnection: false);
            context.Database.UseTransaction(Transaction);
        }
        catch
        {
            context.Dispose();
            throw;
        }

        _contexts.Add(context);
        return context;
    }

    /// <summary>Rolls back everything the scope's contexts wrote, and releases the connection.</summary>
    /// <returns>A task that completes once the transaction is rolled back.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Every step is attempted whatever an earlier one threw: a context that fails to dispose must not
        // skip the rollback, or its writes stay and the transaction left open on the shared connection
        // keeps the next scope from starting one.
        List<Exception>? errors = null;
        foreach (var context in _contexts)
        {
            await Attempt(async () => await context.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        }

        await Attempt(() => _transaction.RollbackAsync()).ConfigureAwait(false);
        await Attempt(async () => await _transaction.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        await Attempt(async () => await _root.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
        ThrowAll(errors);

        async Task Attempt(Func<Task> step)
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
    }

    /// <summary>Rolls back everything the scope's contexts wrote, and releases the connection.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // As DisposeAsync: every step, whatever an earlier one threw.
        List<Exception>? errors = null;
        foreach (var context in _contexts)
        {
            Attempt(context.Dispose);
        }

        Attempt(_transaction.Rollback);
        Attempt(_transaction.Dispose);
        Attempt(_root.Dispose);
        ThrowAll(errors);

        void Attempt(Action step)
        {
            try
            {
                step();
            }
            catch (Exception error)
            {
                (errors ??= []).Add(error);
            }
        }
    }

    // One failure is rethrown as it was; several together.
    private static void ThrowAll(List<Exception>? errors)
    {
        switch (errors)
        {
            case null or []:
                return;
            case [var only]:
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(only).Throw();
                return;
            default:
                throw new AggregateException("Disposing the isolated scope failed more than once.", errors);
        }
    }
}
