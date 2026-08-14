using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Lines a capture source's columns up with what the model says to record.
/// </summary>
/// <remarks>
///     <para>
///         A source supplies whatever it happens to have — the change tracker supplies every mapped
///         property, a bulk update supplies the columns it wrote — and the plan says what should be
///         recorded. Working out the intersection is per-source work, not per-row work, so it
///         happens once here.
///     </para>
///     <para>
///         Columns come out sorted by payload name, which is what makes two sources describing the
///         same change produce byte-identical payloads. Without it a <c>SaveChanges</c> entry and a
///         bulk entry for the same row would differ only in key order — indistinguishable to a JSON
///         reader, and a permanent obstacle to comparing them.
///     </para>
/// </remarks>
internal sealed class AuditSourceProjection
{
    private AuditSourceProjection(
        AuditEntityPlan plan,
        IReadOnlyList<ProjectedColumn> columns,
        IReadOnlyList<KeyColumn> entityKey,
        IReadOnlyList<KeyColumn> payloadKey)
    {
        Plan = plan;
        Columns = columns;
        EntityKey = entityKey;
        PayloadKey = payloadKey;
    }

    public AuditEntityPlan Plan { get; }

    /// <summary>The recorded columns, sorted by payload name.</summary>
    public IReadOnlyList<ProjectedColumn> Columns { get; }

    /// <summary>How to read the components of the entry's key column.</summary>
    public IReadOnlyList<KeyColumn> EntityKey { get; }

    /// <summary>How to read the primary key written into the payload.</summary>
    public IReadOnlyList<KeyColumn> PayloadKey { get; }

    public static AuditSourceProjection Create(IAuditCaptureSource source, AuditEntityPlan plan)
    {
        var indexes = new Dictionary<IProperty, int>(source.Properties.Count);
        for (var i = 0; i < source.Properties.Count; i++)
        {
            indexes[source.Properties[i]] = i;
        }

        var columns = new List<ProjectedColumn>(source.Properties.Count);
        for (var i = 0; i < source.Properties.Count; i++)
        {
            if (plan.Capture(source.Properties[i]) is { } capture)
            {
                columns.Add(new ProjectedColumn(i, capture));
            }
        }

        columns.Sort(static (a, b) => string.CompareOrdinal(a.Plan.Name, b.Plan.Name));

        return new AuditSourceProjection(
            plan,
            columns,
            [.. plan.KeyProperties.Select(p => KeyColumn.For(p, indexes))],
            [.. plan.PrimaryKey.Select(p => KeyColumn.For(p, indexes))]);
    }
}

/// <summary>A recorded column, and where the source keeps it.</summary>
/// <param name="Index">Index into the source's property list.</param>
/// <param name="Plan">How the property is recorded.</param>
internal readonly record struct ProjectedColumn(int Index, AuditPropertyPlan Plan);

/// <summary>A key component, and how to read it.</summary>
/// <param name="Property">The key property.</param>
/// <param name="Index">
///     Index into the source's property list, or <c>-1</c> when the source does not carry it.
/// </param>
/// <param name="Getter">
///     Reads it off the entity instead, for a source that carries the object but not the column.
///     <see langword="null" /> for a shadow property, which has no CLR member to read.
/// </param>
internal readonly record struct KeyColumn(IProperty Property, int Index, Func<object, object?>? Getter)
{
    public static KeyColumn For(IProperty property, Dictionary<IProperty, int> indexes)
        => indexes.TryGetValue(property, out var index)
            ? new KeyColumn(property, index, null)
            : new KeyColumn(property, -1, AuditValues.Getter(property));

    /// <summary>Reads the value, preferring the source over the entity.</summary>
    public object? Read(IAuditCaptureSource source, int row)
    {
        if (Index >= 0)
        {
            return source.GetCurrentValue(row, Index);
        }

        var entity = source.GetEntity(row);

        return entity is not null && Getter is not null ? Getter(entity) : null;
    }
}
