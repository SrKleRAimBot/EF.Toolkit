using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Infrastructure;

/// <summary>
///     A capture source built by hand, so the payload writer can be tested without a database.
/// </summary>
/// <remarks>
///     Written out rather than mocked, matching how EF.Toolkit.Bulk's unit tests supply their fake
///     modification commands. It implements only what is read, and the values are whatever the test
///     put there — which is the point: the writer's job is to turn values and metadata into a
///     payload, and neither has to come from a server.
/// </remarks>
public sealed class FakeCaptureSource : IAuditCaptureSource
{
    private readonly List<object?[]> _current = [];
    private readonly List<object?[]> _original = [];

    public FakeCaptureSource(
        IEntityType entityType,
        AuditOperation operation,
        params IProperty[] properties)
    {
        EntityType = entityType;
        Operation = operation;
        Properties = properties;
    }

    public IEntityType EntityType { get; }

    public AuditOperation Operation { get; }

    public string Source { get; set; } = "Test";

    public IReadOnlyList<IProperty> Properties { get; }

    public int RowCount => _current.Count;

    public bool HasOriginalValues { get; private set; }

    /// <summary>Adds a row whose values are its only image — an insert or a delete.</summary>
    public FakeCaptureSource Row(params object?[] values)
    {
        _current.Add(values);
        _original.Add(values);
        return this;
    }

    /// <summary>Adds a row that changed, with both images.</summary>
    public FakeCaptureSource Changed(object?[] before, object?[] after)
    {
        _original.Add(before);
        _current.Add(after);
        HasOriginalValues = true;
        return this;
    }

    public object? GetCurrentValue(int row, int propertyIndex) => _current[row][propertyIndex];

    public object? GetOriginalValue(int row, int propertyIndex) => _original[row][propertyIndex];

    public object? GetEntity(int row) => null;

    public string? GetTenantId(int row) => null;
}
