using EFToolkit.Bulk.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Planning;

/// <summary>
///     Groups the commands accumulated in one batch into <see cref="BulkPartition" />s.
/// </summary>
/// <remarks>
///     <para>
///         <strong>Why reordering here is safe.</strong>
///         <see cref="ICommandBatchPreparer" /> topologically sorts modification commands and emits
///         them as sets whose members have no dependencies on one another; a batch is only ever
///         filled from a single such set. Regrouping commands <em>within</em> a batch therefore
///         cannot violate a foreign-key, unique-index or same-row ordering constraint. Regrouping
///         <em>across</em> batches would, which is why this operates on one batch at a time and
///         never buffers past <see cref="ModificationCommandBatch.Complete" />.
///     </para>
/// </remarks>
public static class BulkPartitioner
{
    /// <summary>
    ///     Partitions <paramref name="commands" /> by target table, entity state and column shape.
    /// </summary>
    /// <param name="commands">The commands accumulated in one batch.</param>
    /// <param name="options">The context's EF.Toolkit.Bulk settings.</param>
    /// <returns>
    ///     The partitions, in order of first appearance so that behaviour is deterministic and
    ///     diagnostics are readable.
    /// </returns>
    public static IReadOnlyList<BulkPartition> Partition(
        IReadOnlyList<IReadOnlyModificationCommand> commands,
        BulkOptions options)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(options);

        // The first command of each group is the group's key: it already carries every field the
        // shape is made of, so no key object is built and nothing is allocated per row. The
        // parallel list preserves first-appearance order, which a dictionary does not promise.
        var groups = new Dictionary<IReadOnlyModificationCommand, List<IReadOnlyModificationCommand>>(
            ModificationCommandShapeComparer.Instance);

        var order = new List<List<IReadOnlyModificationCommand>>();

        foreach (var command in commands)
        {
            if (!groups.TryGetValue(command, out var group))
            {
                group = [];
                groups[command] = group;
                order.Add(group);
            }

            group.Add(command);
        }

        var partitions = new List<BulkPartition>(order.Count);
        foreach (var group in order)
        {
            var first = group[0];

            partitions.Add(new BulkPartition(
                first.Schema,
                first.TableName,
                first.EntityState,
                group,
                IneligibleReason(first),
                belowThreshold: group.Count < options.Threshold));
        }

        return partitions;
    }

    /// <summary>
    ///     Returns why a command's shape cannot be bulk-executed, or <see langword="null" />.
    /// </summary>
    private static string? IneligibleReason(IReadOnlyModificationCommand command)
    {
        if (command.StoreStoredProcedure is not null)
        {
            return $"'{command.TableName}' is mapped to a stored procedure.";
        }

        foreach (var column in command.ColumnModifications)
        {
            if (column.JsonPath is not null)
            {
                return $"'{command.TableName}'.'{column.ColumnName}' is a JSON column "
                    + "updated by path.";
            }
        }

        if (command.EntityState is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            return $"entity state {command.EntityState} has no bulk equivalent.";
        }

        return null;
    }
}
