using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     A snapshot of what the change tracker held for one entity type, taken before the save.
/// </summary>
/// <remarks>
///     <para>
///         A snapshot rather than a view over the entries, because the entries do not survive the
///         save intact. <c>AcceptAllChanges</c> runs before <c>SavedChanges</c> fires: original
///         values are overwritten with current ones, and deleted entries are detached, taking the
///         only copy of the row that was removed with them. Reading values afterwards would record
///         an update as having changed nothing and a delete as having deleted nothing.
///     </para>
///     <para>
///         The one thing that cannot be read before the save is a store-generated value, which is a
///         placeholder until the database supplies it. Those slots are re-read afterwards by
///         <see cref="RefreshGeneratedValues" />, which is the whole reason capture is split across
///         the two interception points rather than done in either one alone.
///     </para>
///     <para>
///         Owned references that share the owner's table are captured into the same rows, under
///         their navigation path. The column layout is therefore the owner's properties followed by
///         each fold's, and <see cref="_sections" /> records where each begins.
///     </para>
/// </remarks>
internal sealed class ChangeTrackerCaptureSource : IAuditCaptureSource
{
    private readonly IProperty[] _properties;
    private readonly object?[] _current;
    private readonly object?[]? _original;
    private readonly object?[] _entities;
    private readonly string?[] _tenants;

    // One entry per section per row: the owner's entry, then each fold's owned entry. Kept only so
    // that store-generated values can be re-read once the save has produced them.
    private readonly EntityEntry?[] _sectionEntries;
    private readonly Section[] _sections;

    private ChangeTrackerCaptureSource(
        IEntityType entityType,
        AuditOperation operation,
        IProperty[] properties,
        object?[] current,
        object?[]? original,
        object?[] entities,
        string?[] tenants,
        EntityEntry?[] sectionEntries,
        Section[] sections)
    {
        EntityType = entityType;
        Operation = operation;
        _properties = properties;
        _current = current;
        _original = original;
        _entities = entities;
        _tenants = tenants;
        _sectionEntries = sectionEntries;
        _sections = sections;
    }

    /// <inheritdoc />
    public IEntityType EntityType { get; }

    /// <inheritdoc />
    public AuditOperation Operation { get; }

    /// <inheritdoc />
    public string Source => AuditSources.SaveChanges;

    /// <inheritdoc />
    public IReadOnlyList<IProperty> Properties => _properties;

    /// <inheritdoc />
    public int RowCount => _entities.Length;

    /// <inheritdoc />
    public bool HasOriginalValues => _original is not null;

    /// <inheritdoc />
    public object? GetCurrentValue(int row, int propertyIndex)
        => _current[(row * _properties.Length) + propertyIndex];

    /// <inheritdoc />
    public object? GetOriginalValue(int row, int propertyIndex)
        => _original is null
            ? GetCurrentValue(row, propertyIndex)
            : _original[(row * _properties.Length) + propertyIndex];

    /// <inheritdoc />
    public object? GetEntity(int row) => _entities[row];

    /// <inheritdoc />
    public string? GetTenantId(int row) => _tenants[row];

    /// <summary>Takes the snapshot for one entity type and operation.</summary>
    /// <param name="entityType">The entity type.</param>
    /// <param name="operation">What is happening to the rows.</param>
    /// <param name="owners">The entries to capture, one per row.</param>
    /// <param name="plan">What to capture.</param>
    public static ChangeTrackerCaptureSource Create(
        IEntityType entityType,
        AuditOperation operation,
        IReadOnlyList<EntityEntry> owners,
        AuditEntityPlan plan)
    {
        var sections = new Section[1 + plan.OwnedFolds.Count];
        var properties = new List<IProperty>(plan.OwnProperties);

        sections[0] = new Section(0, plan.OwnProperties.Count);

        for (var i = 0; i < plan.OwnedFolds.Count; i++)
        {
            var fold = plan.OwnedFolds[i];
            sections[i + 1] = new Section(properties.Count, fold.Properties.Count);
            properties.AddRange(fold.Properties);
        }

        var width = properties.Count;
        var rows = owners.Count;

        var current = new object?[rows * width];
        var original = operation == AuditOperation.Update ? new object?[rows * width] : null;
        var entities = new object?[rows];
        var tenants = new string?[rows];
        var sectionEntries = new EntityEntry?[rows * sections.Length];

        for (var row = 0; row < rows; row++)
        {
            var owner = owners[row];
            entities[row] = owner.Entity;
            tenants[row] = Tenant(owner, plan);

            var offset = row * width;
            var sectionBase = row * sections.Length;

            sectionEntries[sectionBase] = owner;
            Capture(owner, plan.OwnProperties, current, original, offset + sections[0].Start);

            for (var i = 0; i < plan.OwnedFolds.Count; i++)
            {
                var fold = plan.OwnedFolds[i];
                var owned = Resolve(owner, fold);

                sectionEntries[sectionBase + i + 1] = owned;
                Capture(owned, fold.Properties, current, original, offset + sections[i + 1].Start);
            }
        }

        return new ChangeTrackerCaptureSource(
            entityType, operation, [.. properties], current, original, entities, tenants,
            sectionEntries, sections);
    }

    /// <summary>
    ///     Re-reads the values only the database could supply, once it has.
    /// </summary>
    /// <remarks>
    ///     Store-generated keys, computed columns and database defaults are placeholders until the
    ///     save completes. Deleted entries are detached by then and have nothing left to re-read,
    ///     which is fine — nothing about a deleted row is generated by the statement that removes
    ///     it.
    /// </remarks>
    public void RefreshGeneratedValues()
    {
        if (Operation == AuditOperation.Delete)
        {
            return;
        }

        var width = _properties.Length;

        for (var row = 0; row < _entities.Length; row++)
        {
            var offset = row * width;
            var sectionBase = row * _sections.Length;

            for (var i = 0; i < _sections.Length; i++)
            {
                var entry = _sectionEntries[sectionBase + i];

                if (entry is null || entry.State == EntityState.Detached)
                {
                    continue;
                }

                var section = _sections[i];

                for (var p = section.Start; p < section.Start + section.Count; p++)
                {
                    if (_properties[p].ValueGenerated != ValueGenerated.Never)
                    {
                        _current[offset + p] = entry.CurrentValues[_properties[p]];
                    }
                }
            }
        }
    }

    private static void Capture(
        EntityEntry? entry,
        IReadOnlyList<IProperty> properties,
        object?[] current,
        object?[]? original,
        int start)
    {
        if (entry is null)
        {
            // A null owned reference. Every one of its columns is null in the row, and leaving the
            // slots at their default says exactly that.
            return;
        }

        for (var i = 0; i < properties.Count; i++)
        {
            current[start + i] = entry.CurrentValues[properties[i]];

            if (original is not null)
            {
                // An owned part that was itself added has no before-image, so its "before" is its
                // "after" and the property simply compares equal.
                original[start + i] = entry.State == EntityState.Added
                    ? entry.CurrentValues[properties[i]]
                    : entry.OriginalValues[properties[i]];
            }
        }
    }

    private static EntityEntry? Resolve(EntityEntry owner, AuditOwnedFold fold)
    {
        var entry = owner;

        foreach (var navigation in fold.Path)
        {
            var target = entry.Reference(navigation.Name).TargetEntry;

            if (target is null)
            {
                return null;
            }

            entry = target;
        }

        return entry;
    }

    private static string? Tenant(EntityEntry entry, AuditEntityPlan plan)
    {
        if (plan.TenantProperty is not { } property)
        {
            return null;
        }

        var text = AuditValues.ToKeyText(entry.CurrentValues[property]);

        return text.Length == 0 ? null : text;
    }

    /// <summary>Where one entry's properties sit in the column layout.</summary>
    private readonly record struct Section(int Start, int Count);
}
