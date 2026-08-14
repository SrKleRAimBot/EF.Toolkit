using EFToolkit.Audit.Api;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Bulk;

/// <summary>
///     Presents part of a bulk write as something the audit entry factory can read.
/// </summary>
/// <remarks>
///     <para>
///         "Part of", because one bulk operation is not always one kind of change. A merge inserts
///         some rows and updates others, and a synchronise also deletes rows nothing in the source
///         named. Each of those becomes a source of its own, so the entries that come out say what
///         actually happened to each row rather than what the call was called.
///     </para>
///     <para>
///         The property list is the whole row when before-images were captured, and only the columns
///         the operation touched when they were not. That is what lets a bulk-updated row's audit
///         entry carry the same old-to-new diff a <c>SaveChanges</c>-updated row's does: the
///         before-image supplies every column, and the written values overwrite the ones that
///         changed.
///     </para>
/// </remarks>
internal sealed class BulkCaptureSource : IAuditCaptureSource
{
    private readonly BulkWriteObservation _observation;
    private readonly int[] _rows;
    private readonly IProperty[] _properties;
    private readonly int[] _newColumns;
    private readonly int[] _oldColumns;

    private BulkCaptureSource(
        BulkWriteObservation observation,
        AuditOperation operation,
        string source,
        int[] rows,
        IProperty[] properties,
        int[] newColumns,
        int[] oldColumns,
        bool hasOriginalValues)
    {
        _observation = observation;
        _rows = rows;
        _properties = properties;
        _newColumns = newColumns;
        _oldColumns = oldColumns;

        Operation = operation;
        Source = source;
        HasOriginalValues = hasOriginalValues;
    }

    /// <inheritdoc />
    public IEntityType EntityType => _observation.EntityType;

    /// <inheritdoc />
    public AuditOperation Operation { get; }

    /// <inheritdoc />
    public string Source { get; }

    /// <inheritdoc />
    public IReadOnlyList<IProperty> Properties => _properties;

    /// <inheritdoc />
    public int RowCount => _rows.Length;

    /// <inheritdoc />
    public bool HasOriginalValues { get; }

    /// <inheritdoc />
    public object? GetCurrentValue(int row, int propertyIndex)
    {
        if (_newColumns[propertyIndex] >= 0)
        {
            return _observation.GetValue(_rows[row], _newColumns[propertyIndex]);
        }

        // A column the operation did not write still has a value, and the before-image is it. This
        // is what makes an insert-only column or an untouched column read correctly in the "after"
        // half of the payload instead of appearing as null.
        return _oldColumns[propertyIndex] >= 0
            ? _observation.GetBeforeImageValue(_rows[row], _oldColumns[propertyIndex])
            : null;
    }

    /// <inheritdoc />
    public object? GetOriginalValue(int row, int propertyIndex)
        => _oldColumns[propertyIndex] >= 0
            ? _observation.GetBeforeImageValue(_rows[row], _oldColumns[propertyIndex])
            : GetCurrentValue(row, propertyIndex);

    /// <inheritdoc />
    public object? GetEntity(int row) => _observation.Entities[_rows[row]];

    /// <inheritdoc />
    /// <remarks>
    ///     Always null. The tenant is a column like any other here, and the factory reads it out of
    ///     <see cref="Properties" /> when the model says which one it is.
    /// </remarks>
    public string? GetTenantId(int row) => null;

    /// <summary>Builds the sources for one observed bulk write.</summary>
    /// <param name="observation">What the operation wrote.</param>
    /// <returns>One source per kind of change the operation actually made.</returns>
    public static List<IAuditCaptureSource> For(BulkWriteObservation observation)
    {
        var source = SourceName(observation.Operation);

        return observation.Operation switch
        {
            BulkOperationKind.Insert =>
                [Single(observation, AuditOperation.Insert, source, All(observation.RowCount))],

            BulkOperationKind.Update =>
                [Single(observation, AuditOperation.Update, source, All(observation.RowCount))],

            BulkOperationKind.Delete =>
                [Single(observation, AuditOperation.Delete, source, All(observation.RowCount))],

            _ => Upsert(observation, source),
        };
    }

    /// <summary>
    ///     Splits a merge or synchronise into the changes it actually made.
    /// </summary>
    /// <remarks>
    ///     A source row the before-image read did not match is a row the target did not hold, so the
    ///     operation went on to insert it. That is the per-row split, derived from a read that had
    ///     to happen anyway — and the reason auditing a merge requires before-images rather than
    ///     merely preferring them.
    /// </remarks>
    private static List<IAuditCaptureSource> Upsert(BulkWriteObservation observation, string source)
    {
        if (!observation.HasBeforeImages)
        {
            throw new AuditNotSupportedException(
                $"Auditing a {observation.Operation.ToString().ToLowerInvariant()} of "
                + $"'{observation.EntityType.DisplayName()}' needs the rows as they were, because "
                + "that is what says which of them the operation inserted and which it updated. "
                + "Remove WithoutBeforeImages() from the call, or CaptureBeforeImages(false) from "
                + "the configuration.");
        }

        List<int> inserted = [];
        List<int> updated = [];

        for (var row = 0; row < observation.RowCount; row++)
        {
            (observation.HasBeforeImage(row) ? updated : inserted).Add(row);
        }

        var sources = new List<IAuditCaptureSource>(3);

        if (inserted.Count > 0)
        {
            sources.Add(Single(observation, AuditOperation.Insert, source, [.. inserted]));
        }

        if (updated.Count > 0)
        {
            sources.Add(Single(observation, AuditOperation.Update, source, [.. updated]));
        }

        if (observation.RemovedRows.Count > 0)
        {
            sources.Add(new RemovedRowCaptureSource(observation, source));
        }

        return sources;
    }

    private static BulkCaptureSource Single(
        BulkWriteObservation observation,
        AuditOperation operation,
        string source,
        int[] rows)
    {
        var properties = new List<IProperty>();
        var newColumns = new List<int>();
        var oldColumns = new List<int>();

        if (observation.HasBeforeImages)
        {
            // The whole row, with the written columns layered over it.
            for (var i = 0; i < observation.BeforeImageColumns.Count; i++)
            {
                if (observation.BeforeImageColumns[i].Property is not { } property)
                {
                    continue;
                }

                properties.Add(property);
                oldColumns.Add(i);
                newColumns.Add(Written(observation, property));
            }
        }
        else
        {
            for (var i = 0; i < observation.Columns.Count; i++)
            {
                if (observation.Columns[i].Property is not { } property)
                {
                    continue;
                }

                properties.Add(property);
                newColumns.Add(i);
                oldColumns.Add(-1);
            }
        }

        // An insert has no earlier state, and neither does anything captured without before-images.
        // A delete's only image is the one that was read, so its "after" is its "before".
        var hasOriginals = observation.HasBeforeImages && operation != AuditOperation.Insert;

        if (operation == AuditOperation.Delete && observation.HasBeforeImages)
        {
            // Nothing was written, so every column reads from the before-image.
            for (var i = 0; i < newColumns.Count; i++)
            {
                newColumns[i] = -1;
            }
        }

        return new BulkCaptureSource(
            observation, operation, source, rows, [.. properties], [.. newColumns],
            [.. oldColumns], hasOriginals);
    }

    private static int Written(BulkWriteObservation observation, IProperty property)
    {
        for (var i = 0; i < observation.Columns.Count; i++)
        {
            if (observation.Columns[i].Property == property && observation.Columns[i].IsWrite)
            {
                return i;
            }
        }

        return -1;
    }

    private static int[] All(int count)
    {
        var rows = new int[count];
        for (var i = 0; i < count; i++)
        {
            rows[i] = i;
        }

        return rows;
    }

    private static string SourceName(BulkOperationKind kind)
        => kind switch
        {
            BulkOperationKind.Insert => AuditSources.BulkInsert,
            BulkOperationKind.Update => AuditSources.BulkUpdate,
            BulkOperationKind.Delete => AuditSources.BulkDelete,
            BulkOperationKind.Merge => AuditSources.BulkMerge,
            _ => AuditSources.BulkSynchronize,
        };
}
