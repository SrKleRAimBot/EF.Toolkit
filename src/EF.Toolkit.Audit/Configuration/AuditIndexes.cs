namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Which indexes are created on the audit table.
/// </summary>
/// <remarks>
///     All of them, by default. An audit table that is only ever inserted into needs none, and an
///     audit table that is ever read needs most of these — and finding that out after it has grown
///     to a hundred million rows is an expensive way to learn it.
/// </remarks>
[Flags]
public enum AuditIndexes
{
    /// <summary>No indexes beyond the primary key.</summary>
    None = 0,

    /// <summary>
    ///     <c>(EntityType, EntityKey, OccurredAt DESC)</c> — the history of one row.
    /// </summary>
    /// <remarks>The dominant query, and the one an audit trail is usually built for.</remarks>
    History = 1,

    /// <summary><c>(TenantId, OccurredAt DESC)</c>. Created only when multi-tenancy is configured.</summary>
    Tenant = 2,

    /// <summary><c>(ActorId, OccurredAt DESC)</c> — everything one principal did.</summary>
    Actor = 4,

    /// <summary><c>(CorrelationId)</c> — everything one unit of work did.</summary>
    Correlation = 8,

    /// <summary>
    ///     An index over the change payload — a GIN index on PostgreSQL's <c>jsonb</c>.
    /// </summary>
    /// <remarks>
    ///     This is what makes "which orders were ever shipped by this actor" answerable without a
    ///     sequential scan. It has no equivalent on SQL Server, where a hot path is indexed instead
    ///     through a persisted computed column.
    /// </remarks>
    Payload = 16,

    /// <summary>Every index. The default.</summary>
    All = History | Tenant | Actor | Correlation | Payload,
}
