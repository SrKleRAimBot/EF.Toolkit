using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.MySql.Infrastructure;

/// <summary>
///     Wraps the MySQL provider's <see cref="IModificationCommandBatchFactory" /> so that batches
///     created during <c>SaveChanges()</c> can be executed as bulk operations.
/// </summary>
/// <remarks>
///     <para>
///         The SQL Server, PostgreSQL and SQLite packages derive from their provider's factory. This
///         one cannot: Oracle's factory is internal, and other MySQL providers have factories of
///         their own. Wrapping whatever factory the provider registered gets the same thing — a
///         genuine provider batch for every partition EF.Toolkit.Bulk cannot accelerate — without
///         naming any of them.
///     </para>
///     <para>
///         The wrapping itself is done by <see cref="MySqlBulkBatchingExtension" />.
///     </para>
/// </remarks>
public sealed class MySqlBulkModificationCommandBatchFactory : IModificationCommandBatchFactory
{
    private readonly IModificationCommandBatchFactory _provider;
    private readonly BulkOptions _bulkOptions;
    private readonly IBulkOperationExecutor _executor;

    /// <summary>Initializes a new instance of the factory.</summary>
    /// <param name="provider">The factory the MySQL provider registered.</param>
    /// <param name="bulkOptions">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="executor">The MySQL bulk executor.</param>
    public MySqlBulkModificationCommandBatchFactory(
        IModificationCommandBatchFactory provider,
        BulkOptions bulkOptions,
        IBulkOperationExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(bulkOptions);
        ArgumentNullException.ThrowIfNull(executor);

        _provider = provider;
        _bulkOptions = bulkOptions;
        _executor = executor;
    }

    /// <inheritdoc />
    public ModificationCommandBatch Create()
        // The provider's Create is passed as a factory rather than invoked once: a single
        // EF.Toolkit.Bulk batch can fall back to several provider batches, because each partition is
        // replayed separately and the provider still enforces its own per-batch limits within each.
        => new BulkModificationCommandBatch(_provider.Create, _bulkOptions, _executor);
}
