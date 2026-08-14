namespace EFToolkit.Audit.Api;

/// <summary>
///     What happened to a row, as recorded on a single audit entry.
/// </summary>
/// <remarks>
///     The numeric values are persisted, so they are assigned explicitly and must not be renumbered.
///     Zero is deliberately unused: a default-constructed entry that never had its operation set
///     should not look like a valid insert.
/// </remarks>
public enum AuditOperation
{
    /// <summary>The row was inserted.</summary>
    Insert = 1,

    /// <summary>The row was updated.</summary>
    Update = 2,

    /// <summary>The row was deleted.</summary>
    Delete = 3,
}
