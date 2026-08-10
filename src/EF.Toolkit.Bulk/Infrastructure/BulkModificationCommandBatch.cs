using System.Data.Common;
using System.Diagnostics;
using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Diagnostics;
using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Planning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Infrastructure;

/// <summary>
///     Accumulates the modification commands EF Core produces during <c>SaveChanges()</c>, groups
///     them into same-shape partitions, and executes each one either as a native bulk operation or
///     through the database provider's own batch.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Ordering is inherited, not re-derived.</strong>
///         <see cref="ICommandBatchPreparer" /> builds a multigraph over modification commands —
///         foreign-key edges, unique-index edges and same-row edges — topologically sorts it, and
///         fills each batch from a single dependency-independent set. Regrouping within a batch is
///         therefore safe; that guarantee is the reason this type can exist at all.
///     </para>
///     <para>
///         <strong>Bulk is only ever an optimisation.</strong> Any partition that cannot be
///         accelerated is replayed through a genuine provider batch, so results are identical to
///         stock EF Core rather than merely similar.
///     </para>
/// </remarks>
public class BulkModificationCommandBatch : ModificationCommandBatch
{
    private readonly Func<ModificationCommandBatch> _createProviderBatch;
    private readonly IBulkOperationExecutor? _executor;
    private readonly List<IReadOnlyModificationCommand> _commands = [];

    private IReadOnlyList<BulkPartition>? _partitions;
    private bool _completed;
    private bool _areMoreBatchesExpected;

    /// <summary>Initializes a new batch.</summary>
    /// <param name="createProviderBatch">
    ///     Creates a fresh provider-specific batch. Called at execution time rather than once up
    ///     front, because a single EF.Toolkit.Bulk batch can fall back to several provider batches.
    /// </param>
    /// <param name="options">The context's EF.Toolkit.Bulk settings.</param>
    /// <param name="executor">
    ///     The provider's bulk executor, or <see langword="null" /> when the provider has none, in
    ///     which case every partition falls back.
    /// </param>
    public BulkModificationCommandBatch(
        Func<ModificationCommandBatch> createProviderBatch,
        BulkOptions options,
        IBulkOperationExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(createProviderBatch);
        ArgumentNullException.ThrowIfNull(options);

        _createProviderBatch = createProviderBatch;
        _executor = executor;
        Options = options;
    }

    /// <summary>The context's EF.Toolkit.Bulk settings.</summary>
    protected BulkOptions Options { get; }

    /// <inheritdoc />
    public override IReadOnlyList<IReadOnlyModificationCommand> ModificationCommands => _commands;

    /// <inheritdoc />
    public override bool AreMoreBatchesExpected => _areMoreBatchesExpected;

    /// <summary>
    ///     Whether executing this batch needs a transaction.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A batch holding more than one command becomes more than one statement — several bulk
    ///         operations, or a staging table plus a merge — none of which is individually atomic,
    ///         so they must share a transaction to fail as a unit the way stock EF's single command
    ///         would.
    ///     </para>
    ///     <para>
    ///         Counting commands is not enough on its own. A <em>single</em> accelerated command
    ///         still becomes a create, a copy, a set-based statement and a drop, and a bulk copy
    ///         split by batch size commits each batch separately when there is no transaction to
    ///         hold them together. So any partition that will be accelerated needs one, whatever
    ///         the command count. Below the threshold nothing accelerates, which is why the common
    ///         single-row save still skips the transaction entirely.
    ///     </para>
    /// </remarks>
    public override bool RequiresTransaction
        => _commands.Count > 1 || _partitions is null || AnyAccelerable(_partitions);

    private static bool AnyAccelerable(IReadOnlyList<BulkPartition> partitions)
    {
        for (var i = 0; i < partitions.Count; i++)
        {
            if (partitions[i].CanAccelerate)
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public override bool TryAddCommand(IReadOnlyModificationCommand modificationCommand)
    {
        ArgumentNullException.ThrowIfNull(modificationCommand);

        if (_completed)
        {
            return false;
        }

        // EF treats a batch that rejects the very first command it is offered as a fatal error, so
        // the size limit only applies once something is already accumulated.
        if (_commands.Count > 0 && _commands.Count >= Options.MaxBatchSize)
        {
            return false;
        }

        _commands.Add(modificationCommand);
        return true;
    }

    /// <inheritdoc />
    public override void Complete(bool moreBatchesExpected)
    {
        _completed = true;
        _areMoreBatchesExpected = moreBatchesExpected;
        _partitions = BulkPartitioner.Partition(_commands, Options);
        BulkDiagnostics.ReportPartitionsPlanned(_partitions);

        if (Options.OnUnsupported == Unsupported.Throw)
        {
            ThrowIfAnyShapeUnsupported(_partitions);
        }
    }

    /// <inheritdoc />
    public override void Execute(IRelationalConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var partitions = EnsureCompleted();

        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            var moreToFollow = _areMoreBatchesExpected || i < partitions.Count - 1;

            var accelerated = TryAccelerate(partition, out var reason);
            var timestamp = Stopwatch.GetTimestamp();

            if (accelerated)
            {
                var result = WrapStoreErrors(partition, () => _executor!.Execute(ModificationCommandRowSet.Create(partition), connection));
                if (!result.Handled)
                {
                    // The executor needed the database to decide and ruled the partition out. It
                    // guarantees nothing was written, so replaying is safe.
                    accelerated = false;
                    reason = result.DeclinedReason;
                }
            }

            if (!accelerated)
            {
                ThrowIfUnsupported(partition, reason);
                ExecuteThroughProvider(partition.Commands, connection, moreToFollow);
            }

            BulkDiagnostics.ReportPartitionExecuted(
                partition, accelerated, reason, Stopwatch.GetElapsedTime(timestamp));
        }
    }

    /// <inheritdoc />
    public override async Task ExecuteAsync(
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var partitions = EnsureCompleted();

        for (var i = 0; i < partitions.Count; i++)
        {
            var partition = partitions[i];
            var moreToFollow = _areMoreBatchesExpected || i < partitions.Count - 1;

            var accelerated = TryAccelerate(partition, out var reason);
            var timestamp = Stopwatch.GetTimestamp();

            if (accelerated)
            {
                var result = await WrapStoreErrorsAsync(
                        partition,
                        () => _executor!.ExecuteAsync(
                            ModificationCommandRowSet.Create(partition), connection, cancellationToken))
                    .ConfigureAwait(false);

                if (!result.Handled)
                {
                    // The executor needed the database to decide and ruled the partition out. It
                    // guarantees nothing was written, so replaying is safe.
                    accelerated = false;
                    reason = result.DeclinedReason;
                }
            }

            if (!accelerated)
            {
                ThrowIfUnsupported(partition, reason);
                await ExecuteThroughProviderAsync(
                        partition.Commands, connection, moreToFollow, cancellationToken)
                    .ConfigureAwait(false);
            }

            BulkDiagnostics.ReportPartitionExecuted(
                partition, accelerated, reason, Stopwatch.GetElapsedTime(timestamp));
        }
    }

    /// <summary>
    ///     Presents a store failure the way EF Core would.
    /// </summary>
    /// <remarks>
    ///     A bulk copy surfaces the provider's own exception — <c>PostgresException</c>,
    ///     <c>SqlException</c> — whereas EF wraps store failures in a
    ///     <see cref="DbUpdateException" /> carrying the affected entries. Applications catch the
    ///     latter, so without this an EF.Toolkit.Bulk-enabled context would break existing error handling
    ///     the first time a constraint was violated.
    /// </remarks>
    private static BulkExecutionResult WrapStoreErrors(
        BulkPartition partition,
        Func<BulkExecutionResult> execute)
    {
        try
        {
            return execute();
        }
        catch (Exception ex) when (ShouldWrap(ex))
        {
            throw StoreException(partition, ex);
        }
    }

    private static async Task<BulkExecutionResult> WrapStoreErrorsAsync(
        BulkPartition partition,
        Func<Task<BulkExecutionResult>> execute)
    {
        try
        {
            return await execute().ConfigureAwait(false);
        }
        catch (Exception ex) when (ShouldWrap(ex))
        {
            throw StoreException(partition, ex);
        }
    }

    private static bool ShouldWrap(Exception ex)
        => ex is DbException && ex is not DbUpdateException;

    private static DbUpdateException StoreException(BulkPartition partition, Exception inner)
        => new(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            inner,
            [.. partition.Commands.SelectMany(c => c.Entries)]);

    private IReadOnlyList<BulkPartition> EnsureCompleted()
        => _partitions
            ?? throw new InvalidOperationException(
                "The batch was executed before Complete() was called. This indicates a bug in "
                + "EF.Toolkit.Bulk's integration with EF Core's update pipeline.");

    private bool TryAccelerate(BulkPartition partition, out string? reason)
    {
        reason = partition.IneligibleReason;

        if (reason is not null)
        {
            return false;
        }

        if (partition.BelowThreshold)
        {
            // Not a shape problem, so ThrowIfUnsupported ignores it: below the threshold the
            // provider's per-row statements genuinely beat a bulk copy's setup cost.
            reason = $"only {partition.Commands.Count} row(s), below the "
                + $"{Options.Threshold}-row threshold.";
            return false;
        }

        if (_executor is null)
        {
            reason = "the database provider does not supply a bulk executor.";
            return false;
        }

        return _executor.CanExecute(ModificationCommandRowSet.Create(partition), out reason);
    }

    /// <summary>
    ///     Replays a partition through freshly created provider batches, splitting whenever the
    ///     provider's own limits reject a command.
    /// </summary>
    private void ExecuteThroughProvider(
        IReadOnlyList<IReadOnlyModificationCommand> commands,
        IRelationalConnection connection,
        bool moreBatchesExpected)
    {
        var index = 0;
        while (index < commands.Count)
        {
            var batch = FillProviderBatch(commands, ref index, moreBatchesExpected);
            batch.Execute(connection);
        }
    }

    private async Task ExecuteThroughProviderAsync(
        IReadOnlyList<IReadOnlyModificationCommand> commands,
        IRelationalConnection connection,
        bool moreBatchesExpected,
        CancellationToken cancellationToken)
    {
        var index = 0;
        while (index < commands.Count)
        {
            var batch = FillProviderBatch(commands, ref index, moreBatchesExpected);
            await batch.ExecuteAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private ModificationCommandBatch FillProviderBatch(
        IReadOnlyList<IReadOnlyModificationCommand> commands,
        ref int index,
        bool moreBatchesExpected)
    {
        var batch = _createProviderBatch();
        var added = 0;

        while (index < commands.Count && batch.TryAddCommand(commands[index]))
        {
            index++;
            added++;
        }

        if (added == 0)
        {
            throw new InvalidOperationException(
                $"The '{commands[index].TableName}' provider batch rejected the first command it "
                + "was offered, so no progress can be made.");
        }

        // Anything left in this partition, or any partition after it, still has to run.
        batch.Complete(moreBatchesExpected || index < commands.Count);
        return batch;
    }

    private static void ThrowIfAnyShapeUnsupported(IReadOnlyList<BulkPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            if (partition.IneligibleReason is { } reason)
            {
                throw new BulkNotSupportedException(
                    $"EF.Toolkit.Bulk cannot accelerate {partition}: {reason} "
                    + "Configure OnUnsupported(Unsupported.FallBack) to run it through EF Core "
                    + "instead.");
            }
        }
    }

    private void ThrowIfUnsupported(BulkPartition partition, string? reason)
    {
        if (Options.OnUnsupported == Unsupported.Throw && !partition.BelowThreshold)
        {
            throw new BulkNotSupportedException(
                $"EF.Toolkit.Bulk cannot accelerate {partition}: {reason ?? "no reason given."} "
                + "Configure OnUnsupported(Unsupported.FallBack) to run it through EF Core "
                + "instead.");
        }
    }
}
