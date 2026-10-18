using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Infrastructure;
using Microsoft.EntityFrameworkCore.Sqlite.Update.Internal;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Sqlite.Infrastructure;

// EF1001: SqliteModificationCommandBatchFactory lives in the provider's .Internal namespace and
// carries no compatibility guarantee across releases. Deriving from it is the same deliberate,
// contained trade the SQL Server and PostgreSQL packages make: it is the only way to obtain a
// genuine SQLite batch for the fallback path. EF.Toolkit.Bulk pins a single EF major in
// consequence (see Directory.Packages.props), and the equivalence suite catches drift.
#pragma warning disable EF1001

/// <summary>
///     Replaces the SQLite provider's <see cref="IModificationCommandBatchFactory" /> so that
///     batches created during <c>SaveChanges()</c> can be executed as bulk operations.
/// </summary>
/// <remarks>
///     SQLite's own batches hold a single command each — the engine is in-process, so there is no
///     round trip to amortise — which is why the fallback below may create one provider batch per
///     row. EF.Toolkit.Bulk's batch still gathers the whole set first, which is what lets a large
///     save be partitioned and written through one prepared statement instead.
/// </remarks>
public class SqliteBulkModificationCommandBatchFactory : SqliteModificationCommandBatchFactory
{
    private readonly BulkOptions _bulkOptions;
    private readonly IBulkOperationExecutor _executor;

    /// <summary>Initializes a new instance of the factory.</summary>
    /// <param name="dependencies">The provider's batch-factory dependencies.</param>
    /// <param name="bulkOptions">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="executor">The SQLite bulk executor.</param>
    public SqliteBulkModificationCommandBatchFactory(
        ModificationCommandBatchFactoryDependencies dependencies,
        BulkOptions bulkOptions,
        IBulkOperationExecutor executor)
        : base(dependencies)
    {
        _bulkOptions = bulkOptions;
        _executor = executor;
    }

    /// <inheritdoc />
    public override ModificationCommandBatch Create()
        // base.Create is passed as a factory rather than invoked once: a single EF.Toolkit.Bulk batch
        // can fall back to many provider batches, one per command on this provider.
        => new BulkModificationCommandBatch(base.Create, _bulkOptions, _executor);
}
