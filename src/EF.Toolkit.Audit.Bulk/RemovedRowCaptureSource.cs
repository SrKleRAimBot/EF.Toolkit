using EFToolkit.Audit.Api;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Bulk;

/// <summary>
///     The rows a synchronise removed that nothing in its source named.
/// </summary>
/// <remarks>
///     The one set of changes a bulk operation makes that no entity the caller handed over
///     corresponds to. A synchronise's delete arm removes whatever the source omitted, so without
///     this the single operation most capable of deleting rows nobody meant to delete would be the
///     one whose deletions left no trace.
/// </remarks>
internal sealed class RemovedRowCaptureSource : IAuditCaptureSource
{
    private readonly BulkWriteObservation _observation;
    private readonly IProperty[] _properties;
    private readonly int[] _columns;

    public RemovedRowCaptureSource(BulkWriteObservation observation, string source)
    {
        _observation = observation;
        Source = source;

        var properties = new List<IProperty>();
        var columns = new List<int>();

        for (var i = 0; i < observation.BeforeImageColumns.Count; i++)
        {
            if (observation.BeforeImageColumns[i].Property is { } property)
            {
                properties.Add(property);
                columns.Add(i);
            }
        }

        _properties = [.. properties];
        _columns = [.. columns];
    }

    /// <inheritdoc />
    public IEntityType EntityType => _observation.EntityType;

    /// <inheritdoc />
    public AuditOperation Operation => AuditOperation.Delete;

    /// <inheritdoc />
    public string Source { get; }

    /// <inheritdoc />
    public IReadOnlyList<IProperty> Properties => _properties;

    /// <inheritdoc />
    public int RowCount => _observation.RemovedRows.Count;

    /// <inheritdoc />
    /// <remarks>
    ///     The row as it stood is the only image there is, so it serves as both. A delete's payload
    ///     reads the "before" half, and that is what this returns.
    /// </remarks>
    public bool HasOriginalValues => false;

    /// <inheritdoc />
    public object? GetCurrentValue(int row, int propertyIndex)
        => _observation.RemovedRows[row][_columns[propertyIndex]];

    /// <inheritdoc />
    public object? GetOriginalValue(int row, int propertyIndex)
        => GetCurrentValue(row, propertyIndex);

    /// <inheritdoc />
    /// <remarks>Null: these rows were read from the database, not handed over as objects.</remarks>
    public object? GetEntity(int row) => null;

    /// <inheritdoc />
    public string? GetTenantId(int row) => null;
}
