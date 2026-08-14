using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     The rows an update, delete or merge is about to change, as they stand now.
/// </summary>
/// <remarks>
///     <para>
///         PostgreSQL's <c>RETURNING</c> cannot see the pre-update row before version 18, and no
///         engine's <c>RETURNING</c> or <c>OUTPUT</c> can describe a row the operation did not touch.
///         So the before-image is read rather than returned: one <c>SELECT</c> joining the staging
///         table the operation already built to the target, issued inside the same transaction and
///         before the write.
///     </para>
///     <para>
///         It is the same shape for every operation, which is why there is one of these rather than
///         one arrangement per statement — and it carries a second answer for free. A source row the
///         join did not match is a row the target did not hold, which is precisely the
///         insert-versus-update split a merge otherwise only reports in total.
///     </para>
/// </remarks>
public sealed class BulkBeforeImages
{
    private readonly object?[] _values;
    private readonly bool[] _present;
    private readonly List<object?[]> _removed = [];

    internal BulkBeforeImages(IReadOnlyList<BulkColumnInfo> columns, int rowCount)
    {
        Columns = columns;
        _values = new object?[rowCount * columns.Count];
        _present = new bool[rowCount];
    }

    /// <summary>The columns that were read.</summary>
    public IReadOnlyList<BulkColumnInfo> Columns { get; }

    /// <summary>
    ///     Rows the operation removed that no source row corresponds to.
    /// </summary>
    /// <remarks>
    ///     Only a synchronise produces these: its delete arm removes whatever the source omitted, so
    ///     the rows it takes away are precisely the ones nothing in the caller's list describes.
    ///     Without capturing them here, the one operation that can delete rows nobody named would be
    ///     the one operation whose deletions leave no trace.
    /// </remarks>
    public IReadOnlyList<object?[]> RemovedRows => _removed;

    /// <summary>Records a row the operation is about to remove with no source row of its own.</summary>
    /// <param name="values">Provider values, in <see cref="Columns" /> order.</param>
    public void AddRemovedRow(object?[] values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var converted = new object?[Columns.Count];
        for (var i = 0; i < Columns.Count; i++)
        {
            converted[i] = Columns[i].FromProviderValue(values[i]);
        }

        _removed.Add(converted);
    }

    /// <summary>Whether the target held a row for this source row.</summary>
    /// <param name="row">Zero-based row index.</param>
    public bool HasRow(int row) => _present[row];

    /// <summary>Reads a captured value.</summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    public object? GetValue(int row, int column) => _values[(row * Columns.Count) + column];

    /// <summary>Records a value read from the target.</summary>
    /// <param name="row">Zero-based row index, from the staging ordinal.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    /// <param name="value">The provider value.</param>
    public void SetValue(int row, int column, object? value)
    {
        _present[row] = true;
        _values[(row * Columns.Count) + column] = Columns[column].FromProviderValue(value);
    }

    /// <summary>
    ///     The columns to read for <paramref name="entityType" /> — the whole row.
    /// </summary>
    /// <remarks>
    ///     The whole row rather than only the columns being written, because the point of a
    ///     before-image is to describe what was there. A delete is the clear case: the operation
    ///     itself knows only the columns that locate the row, and an observer told only those learns
    ///     nothing about what was lost.
    /// </remarks>
    public static IReadOnlyList<BulkColumnInfo> ColumnsFor(IEntityType entityType)
    {
        var mapping = entityType.GetTableMappings().FirstOrDefault()
            ?? throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' is not mapped to a table, so its rows cannot be "
                + "read before they are changed.");

        var columns = new List<BulkColumnInfo>();

        foreach (var columnMapping in mapping.ColumnMappings)
        {
            columns.Add(
                new BulkColumnInfo(
                    columnMapping.Column.Name,
                    columnMapping.Column.StoreTypeMapping,
                    columnMapping.Property,
                    isWrite: false,
                    isRead: false,
                    isKey: columnMapping.Property.IsKey()));
        }

        return columns;
    }
}
