namespace EFToolkit.Audit.Api;

/// <summary>
///     Which operations on an entity type produce an audit entry.
/// </summary>
/// <remarks>
///     Distinct from <see cref="AuditOperation" />, which describes one entry that was written.
///     This is configuration — a set — and is never persisted, so its values are free to move.
/// </remarks>
[Flags]
public enum AuditOperations
{
    /// <summary>Nothing is audited. Equivalent to not registering the type at all.</summary>
    None = 0,

    /// <summary>Inserts are audited.</summary>
    Insert = 1,

    /// <summary>Updates are audited.</summary>
    Update = 2,

    /// <summary>Deletes are audited.</summary>
    Delete = 4,

    /// <summary>Inserts, updates and deletes are all audited. The default.</summary>
    All = Insert | Update | Delete,
}
