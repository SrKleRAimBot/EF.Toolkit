using EFToolkit.Bulk.Execution;
using EFToolkit.Bulk.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Presents a list of entities as an <see cref="IBulkRowSet" />, reading values straight off the
///     objects.
/// </summary>
/// <remarks>
///     This is the explicit bulk API's fast path. Nothing here builds a modification command,
///     constructs a dependency graph or runs a topological sort — measurement put that work at
///     roughly 70% of a transparent save's cost. Ordering is instead the caller's responsibility,
///     handled once per entity type rather than once per row.
/// </remarks>
internal sealed class EntityRowSet : IBulkRowSet
{
    private readonly IReadOnlyList<object> _entities;
    private readonly BulkEntityPlan _plan;

    public EntityRowSet(
        IReadOnlyList<object> entities,
        BulkEntityPlan plan,
        EntityState entityState,
        BulkOperationKind operation,
        MergeCounts mergeCounts)
    {
        _entities = entities;
        _plan = plan;
        EntityState = entityState;
        Operation = operation;
        MergeCounts = mergeCounts;
    }

    public string? Schema => _plan.Schema;
    public string TableName => _plan.TableName;
    public EntityState EntityState { get; }
    public BulkOperationKind Operation { get; }
    public MergeCounts MergeCounts { get; }
    public int RowCount => _entities.Count;
    public IReadOnlyList<BulkColumnInfo> Columns => _plan.Columns;

    /// <remarks>Always empty: the explicit API works from detached objects.</remarks>
    public IReadOnlyList<IUpdateEntry> GetEntries(int row) => [];

    public object? GetValue(int row, int column)
        => _plan.Getters[column](_entities[row]);

    /// <remarks>
    ///     A detached entity keeps no before-image, so the current value is the only one available.
    ///     For a concurrency token that is the right answer anyway: the value on the object is the
    ///     one that was loaded, and the database supplies the next one.
    /// </remarks>
    public object? GetOriginalValue(int row, int column) => GetValue(row, column);

    /// <remarks>
    ///     Writing the value onto the entity is what makes <c>order.Id</c> populated when the call
    ///     returns, independently of whether the caller asked for change tracking. Tracking is the
    ///     expensive part; getting your keys back is not.
    /// </remarks>
    public void SetGeneratedValue(int row, int column, object? value)
    {
        var setter = _plan.Setters[column];
        if (setter is null)
        {
            return;
        }

        var property = Columns[column].Property;
        var clrType = property?.ClrType;

        if (value is not null && clrType is not null)
        {
            var target = Nullable.GetUnderlyingType(clrType) ?? clrType;
            if (value.GetType() != target)
            {
                // Providers widen freely — a bigint from a sequence, a decimal from an identity —
                // so the value is narrowed back to what the property actually declares.
                value = Convert.ChangeType(value, target, provider: null);
            }
        }

        setter(_entities[row], value);
    }
}
