using EFToolkit.Bulk.Planning;
using EFToolkit.Bulk.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Presents a <see cref="BulkPartition" />'s modification commands as an
///     <see cref="IBulkRowSet" />, for the transparent <c>SaveChanges()</c> path.
/// </summary>
internal sealed class ModificationCommandRowSet : IBulkRowSet
{
    private readonly IReadOnlyList<IReadOnlyModificationCommand> _commands;
    private readonly int[] _indices;

    private ModificationCommandRowSet(
        BulkPartition partition,
        IReadOnlyList<BulkColumnInfo> columns,
        int[] indices)
    {
        _commands = partition.Commands;
        _indices = indices;

        Schema = partition.Schema;
        TableName = partition.TableName;
        EntityState = partition.EntityState;
        Columns = columns;
    }

    public string? Schema { get; }
    public string TableName { get; }
    public EntityState EntityState { get; }

    /// <remarks>
    ///     A modification command never represents an upsert: EF resolves inserts and updates
    ///     before it builds one.
    /// </remarks>
    public BulkOperationKind Operation => EntityState switch
    {
        EntityState.Added => BulkOperationKind.Insert,
        EntityState.Modified => BulkOperationKind.Update,
        EntityState.Deleted => BulkOperationKind.Delete,
        _ => throw new BulkNotSupportedException($"{EntityState} has no bulk equivalent.")
    };
    public IReadOnlyList<BulkColumnInfo> Columns { get; }
    public int RowCount => _commands.Count;

    /// <remarks>Never consulted: SaveChanges resolves inserts and updates before it gets here.</remarks>
    public MergeCounts MergeCounts => MergeCounts.Exact;

    /// <summary>Builds a row set over <paramref name="partition" />.</summary>
    public static ModificationCommandRowSet Create(BulkPartition partition)
    {
        var template = partition.Commands[0];
        var columns = new List<BulkColumnInfo>(template.ColumnModifications.Count);
        var indices = new int[template.ColumnModifications.Count];

        for (var i = 0; i < template.ColumnModifications.Count; i++)
        {
            var modification = template.ColumnModifications[i];

            columns.Add(new BulkColumnInfo(
                modification.ColumnName,
                modification.TypeMapping,
                modification.Property,
                modification.IsWrite,
                modification.IsRead,
                modification.IsKey,
                modification.IsCondition));

            indices[i] = i;
        }

        return new ModificationCommandRowSet(partition, columns, indices);
    }

    public IReadOnlyList<IUpdateEntry> GetEntries(int row) => _commands[row].Entries;

    /// <remarks>
    ///     Condition columns locate the row and so carry the value as it was loaded, which EF holds
    ///     in <see cref="IColumnModification.OriginalValue" />. Reading
    ///     <see cref="IColumnModification.Value" /> for them returns null on a delete, where nothing
    ///     is being written — a join on those keys then matches no rows at all.
    /// </remarks>
    public object? GetValue(int row, int column)
    {
        var modification = Resolve(row, column);

        // A column that is only a condition carries no new value -- on a delete, Value is null for
        // every key -- so the loaded value is the one to use.
        return modification.IsWrite ? modification.Value : modification.OriginalValue;
    }

    /// <inheritdoc />
    public object? GetOriginalValue(int row, int column) => Resolve(row, column).OriginalValue;

    /// <remarks>
    ///     Assigning <see cref="IColumnModification.Value" /> is what keeps change tracking intact:
    ///     EF's own <c>PropagateResults</c> uses the same setter, which records the value as
    ///     store-generated on the tracked entry.
    /// </remarks>
    public void SetGeneratedValue(int row, int column, object? value)
        => Resolve(row, column).Value = value;

    private IColumnModification Resolve(int row, int column)
    {
        var modifications = _commands[row].ColumnModifications;
        var index = _indices[column];
        var name = Columns[column].Name;

        // Partitioning guarantees a uniform shape, so the cached index is right in every observed
        // case; the name check keeps a partitioning bug from silently writing into the wrong
        // column, which no type error would reveal.
        if ((uint)index < (uint)modifications.Count
            && string.Equals(modifications[index].ColumnName, name, StringComparison.Ordinal))
        {
            return modifications[index];
        }

        foreach (var modification in modifications)
        {
            if (string.Equals(modification.ColumnName, name, StringComparison.Ordinal))
            {
                return modification;
            }
        }

        throw new BulkNotSupportedException(
            $"Command for '{TableName}' has no column '{name}', so the partition is not uniformly "
            + "shaped. This indicates a bug in EF.Toolkit.Bulk's partitioning.");
    }
}
