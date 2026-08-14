using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     What one explicit bulk operation wrote.
/// </summary>
/// <remarks>
///     Column-oriented and backed by the arrays the operation already had, so handing it to an
///     observer costs no copying. It is valid only for the duration of the
///     <see cref="IBulkWriteObserver.ObservedAsync" /> call — an observer that needs it afterwards
///     must take its own copy.
/// </remarks>
public sealed class BulkWriteObservation
{
    private readonly IBulkRowSet _rows;
    private readonly BulkBeforeImages? _beforeImages;

    internal BulkWriteObservation(
        IEntityType entityType,
        IBulkRowSet rows,
        IReadOnlyList<object> entities,
        BulkBeforeImages? beforeImages)
    {
        EntityType = entityType;
        Entities = entities;
        _rows = rows;
        _beforeImages = beforeImages;
    }

    /// <summary>The entity type that was written.</summary>
    public IEntityType EntityType { get; }

    /// <summary>What was done to the rows.</summary>
    public BulkOperationKind Operation => _rows.Operation;

    /// <summary>Whether the rows were inserted, updated or deleted.</summary>
    public EntityState EntityState => _rows.EntityState;

    /// <summary>The entities the values came from, one per row.</summary>
    public IReadOnlyList<object> Entities { get; }

    /// <summary>How many rows the operation covered.</summary>
    public int RowCount => _rows.RowCount;

    /// <summary>The columns the operation dealt with.</summary>
    public IReadOnlyList<BulkColumnInfo> Columns => _rows.Columns;

    /// <summary>
    ///     The columns read as they were before the write, or empty when none were requested.
    /// </summary>
    /// <remarks>
    ///     Not the same list as <see cref="Columns" />, and deliberately so. An update's columns are
    ///     the ones it wrote plus the ones that located the row, while a before-image covers the
    ///     whole row — which is what a delete needs, since the columns that located it are the only
    ///     ones the operation itself ever knew about.
    /// </remarks>
    public IReadOnlyList<BulkColumnInfo> BeforeImageColumns
        => _beforeImages?.Columns ?? [];

    /// <summary>Whether before-images were captured at all.</summary>
    public bool HasBeforeImages => _beforeImages is not null;

    /// <summary>Reads a written value.</summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    /// <returns>The CLR value, before any value converter has been applied.</returns>
    public object? GetValue(int row, int column) => _rows.GetValue(row, column);

    /// <summary>Reads a value as it stood before the write.</summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="BeforeImageColumns" />.</param>
    public object? GetBeforeImageValue(int row, int column)
        => _beforeImages?.GetValue(row, column);

    /// <summary>
    ///     Whether the target already held a row for this source row.
    /// </summary>
    /// <param name="row">Zero-based row index.</param>
    /// <remarks>
    ///     This is how a merge's insert-versus-update split is known per row rather than only in
    ///     total: the before-image read matches source rows to target rows, so a row it did not
    ///     match is one the merge went on to insert. Always <see langword="false" /> when no
    ///     before-images were captured.
    /// </remarks>
    public bool HasBeforeImage(int row) => _beforeImages?.HasRow(row) ?? false;

    /// <summary>
    ///     Rows the operation removed that no source row corresponds to, as they stood.
    /// </summary>
    /// <remarks>
    ///     Only a synchronise produces these — its delete arm removes whatever the source omitted —
    ///     and only when before-images were requested. Each array holds provider-converted values in
    ///     <see cref="BeforeImageColumns" /> order.
    /// </remarks>
    public IReadOnlyList<object?[]> RemovedRows => _beforeImages?.RemovedRows ?? [];
}
