using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.SqlServer.Update.Internal;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.SqlServer.Infrastructure;

// EF1001: SqlServerModificationCommandBatchFactory lives in the provider's .Internal namespace and
// carries no compatibility guarantee across releases. Deriving from it is a deliberate, contained
// trade: it is the only way to obtain a genuine SQL Server batch for the fallback path, and the
// alternative -- reimplementing IModificationCommandBatchFactory -- would mean reproducing provider
// behaviour we must match exactly. EF.Toolkit.Bulk pins a single EF major in consequence (see
// Directory.Packages.props), and the equivalence suite runs against real engines to catch drift.
#pragma warning disable EF1001

/// <summary>
///     Replaces the SQL Server provider's <see cref="IModificationCommandBatchFactory" /> so that
///     batches created during <c>SaveChanges()</c> can be executed as bulk operations.
/// </summary>
/// <remarks>
///     Deriving from <see cref="SqlServerModificationCommandBatchFactory" /> rather than
///     implementing <see cref="IModificationCommandBatchFactory" /> directly is deliberate:
///     <c>base.Create()</c> yields a genuine SQL Server batch, which is what any partition EF.Toolkit.Bulk
///     cannot accelerate is delegated to.
/// </remarks>
public class SqlServerBulkModificationCommandBatchFactory : SqlServerModificationCommandBatchFactory
{
    private readonly BulkOptions _bulkOptions;
    private readonly IBulkOperationExecutor _executor;

    /// <summary>Initializes a new instance of the factory.</summary>
    /// <param name="dependencies">The provider's batch-factory dependencies.</param>
    /// <param name="options">The context options.</param>
    /// <param name="bulkOptions">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="executor">The SQL Server bulk executor.</param>
    public SqlServerBulkModificationCommandBatchFactory(
        ModificationCommandBatchFactoryDependencies dependencies,
        IDbContextOptions options,
        BulkOptions bulkOptions,
        IBulkOperationExecutor executor)
        : base(dependencies, options)
    {
        _bulkOptions = bulkOptions;
        _executor = executor;
    }

    /// <inheritdoc />
    public override ModificationCommandBatch Create()
        // base.Create is passed as a factory rather than invoked once: a single EF.Toolkit.Bulk batch can
        // fall back to several provider batches, because each partition is replayed separately and
        // the provider still enforces its own per-batch limits within each.
        => new BulkModificationCommandBatch(base.Create, _bulkOptions, _executor);
}
