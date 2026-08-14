namespace EFToolkit.Audit.Api;

/// <summary>
///     One recorded change to one row.
/// </summary>
/// <remarks>
///     <para>
///         The key type is configurable, which makes the mapped entity generic
///         (<see cref="AuditEntry{TKey}" />). This base carries everything that does not depend on
///         it, so sinks and factories can work in terms of one non-generic type. It is not itself
///         mapped — only the closed generic is.
///     </para>
///     <para>
///         Deliberately a plain class with no shadow properties and no navigations. Audit entries
///         are written on paths that read values straight off the object, including the explicit
///         bulk API, and a shadow property has nothing to read from there.
///     </para>
/// </remarks>
public abstract class AuditEntry
{
    /// <summary>The audited entity type, as configured by <c>StoreEntityTypeAs</c>.</summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    ///     The audited row's primary key, in a canonical single-string form.
    /// </summary>
    /// <remarks>
    ///     A string rather than the real key because one audit table covers every entity type, and
    ///     their keys have no common type. The typed key is also written into the payload's
    ///     <c>key</c> object, so nothing is lost — this column exists to be indexed and compared.
    /// </remarks>
    public string EntityKey { get; set; } = string.Empty;

    /// <summary>What happened to the row.</summary>
    public AuditOperation Operation { get; set; }

    /// <summary>The acting principal's stable identifier.</summary>
    public string? ActorId { get; set; }

    /// <summary>The acting principal's name, as it was at the time of the change.</summary>
    public string? ActorName { get; set; }

    /// <summary>What kind of principal acted — <c>user</c>, <c>service</c>, <c>system</c>.</summary>
    public string? ActorType { get; set; }

    /// <summary>The tenant the audited row belongs to.</summary>
    public string? TenantId { get; set; }

    /// <summary>When the change was made, always in UTC.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Ties this entry to every other entry written in the same unit of work.</summary>
    public Guid? CorrelationId { get; set; }

    /// <summary>
    ///     Which write path produced this entry — <c>SaveChanges</c>, <c>Bulk.Insert</c>,
    ///     <c>Bulk.Merge</c> and so on.
    /// </summary>
    /// <remarks>
    ///     Not needed to read the trail, but decisive when reading it goes wrong: it is the
    ///     difference between "the update was not audited" and "the update was audited by a path
    ///     that captures less".
    /// </remarks>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    ///     The change itself, as JSON.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <see cref="string" /> rather than a <c>JsonDocument</c>: the column type is what
    ///         makes this queryable — <c>jsonb</c> on PostgreSQL — and the provider packages set
    ///         that. A <c>JsonDocument</c> would add an <see cref="IDisposable" /> to an entity for
    ///         no gain and does not map on every provider.
    ///     </para>
    ///     <para>
    ///         The shape is <c>{ "op", "key", "changed", "old", "new", "meta" }</c>, with
    ///         <c>old</c> and <c>new</c> as sibling objects rather than per-property pairs, because
    ///         that is what a containment index can answer:
    ///         <c>changes @&gt; '{"new":{"Status":"Shipped"}}'</c>.
    ///     </para>
    /// </remarks>
    public string Changes { get; set; } = string.Empty;

    /// <summary>This entry's own primary key, boxed.</summary>
    public abstract object? Key { get; }
}

/// <summary>
///     One recorded change to one row, keyed by <typeparamref name="TKey" />.
/// </summary>
/// <typeparam name="TKey">
///     The key type, chosen by <c>Ids&lt;TKey&gt;(...)</c> when auditing is configured. This closed
///     type is what gets mapped.
/// </typeparam>
public class AuditEntry<TKey> : AuditEntry
{
    /// <summary>This entry's primary key.</summary>
    public TKey Id { get; set; } = default!;

    /// <inheritdoc />
    public override object? Key => Id;
}
