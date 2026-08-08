using System.Text;
using EFBulk.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFBulk.Planning;

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
    /// <param name="options">The context's EF.Bulk settings.</param>
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

        var groups = new Dictionary<string, List<IReadOnlyModificationCommand>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var command in commands)
        {
            var key = ShapeKey(command);
            if (!groups.TryGetValue(key, out var group))
            {
                group = [];
                groups[key] = group;
                order.Add(key);
            }

            group.Add(command);
        }

        var partitions = new List<BulkPartition>(order.Count);
        foreach (var key in order)
        {
            var group = groups[key];
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
    ///     Builds the key that decides which commands can share a bulk operation.
    /// </summary>
    /// <remarks>
    ///     The written column list is part of the key, and in its original order rather than
    ///     sorted: a bulk copy writes a positional stream, so two commands that write the same
    ///     columns in a different order cannot share one. Read columns are included because a
    ///     command that needs values back has to take a different execution path from one that does
    ///     not, and the rows-affected column because it drives concurrency checking.
    /// </remarks>
    private static string ShapeKey(IReadOnlyModificationCommand command)
    {
        var sb = new StringBuilder();
        sb.Append(command.Schema ?? "").Append('.').Append(command.TableName)
            .Append('|').Append((int)command.EntityState)
            .Append("|W:");

        foreach (var column in command.ColumnModifications)
        {
            if (column.IsWrite)
            {
                sb.Append(column.ColumnName).Append(',');
            }
        }

        sb.Append("|R:");
        foreach (var column in command.ColumnModifications)
        {
            if (column.IsRead)
            {
                sb.Append(column.ColumnName).Append(',');
            }
        }

        sb.Append("|C:");
        foreach (var column in command.ColumnModifications)
        {
            if (column.IsCondition)
            {
                sb.Append(column.ColumnName).Append(',');
            }
        }

        sb.Append("|RA:").Append(command.RowsAffectedColumn?.Name ?? "");

        return sb.ToString();
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
