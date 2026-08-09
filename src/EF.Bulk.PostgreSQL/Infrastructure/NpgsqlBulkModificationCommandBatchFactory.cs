using EFBulk.Configuration;
using EFBulk.Execution;
using EFBulk.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using Npgsql.EntityFrameworkCore.PostgreSQL.Update.Internal;

namespace EFBulk.PostgreSQL.Infrastructure;

// EF1001: NpgsqlModificationCommandBatchFactory lives in Npgsql's .Internal namespace and carries
// no compatibility guarantee across releases. Deriving from it is a deliberate, contained trade:
// it is the only way to obtain a genuine Npgsql batch for the fallback path, and the alternative
// -- reimplementing IModificationCommandBatchFactory -- would mean reproducing provider behaviour
// we must match exactly. EF.Bulk pins a single EF major in consequence (see Directory.Packages.props),
// and the equivalence suite runs against real engines to catch any drift.
#pragma warning disable EF1001

/// <summary>
///     Replaces Npgsql's <see cref="IModificationCommandBatchFactory" /> so that batches created
///     during <c>SaveChanges()</c> can be executed as bulk operations.
/// </summary>
/// <remarks>
///     Deriving from <see cref="NpgsqlModificationCommandBatchFactory" /> rather than implementing
///     <see cref="IModificationCommandBatchFactory" /> directly is deliberate: <c>base.Create()</c>
///     yields a genuine Npgsql batch, which is what any partition EF.Bulk cannot accelerate is
///     delegated to.
/// </remarks>
public class NpgsqlBulkModificationCommandBatchFactory : NpgsqlModificationCommandBatchFactory
{
    private readonly BulkOptions _bulkOptions;
    private readonly IBulkOperationExecutor _executor;

    /// <summary>Initializes a new instance of the factory.</summary>
    /// <param name="dependencies">Npgsql's batch-factory dependencies.</param>
    /// <param name="options">The context options.</param>
    /// <param name="bulkOptions">The context's EF.Bulk settings.</param>
    /// <param name="executor">The PostgreSQL bulk executor.</param>
    public NpgsqlBulkModificationCommandBatchFactory(
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
        // base.Create is passed as a factory rather than invoked once: a single EF.Bulk batch can
        // fall back to several provider batches, because each partition is replayed separately and
        // Npgsql still enforces its own per-batch limits within each.
        => new BulkModificationCommandBatch(base.Create, _bulkOptions, _executor);
}
