namespace EFToolkit.Audit.Api;

/// <summary>
///     Marks an entity type for auditing.
/// </summary>
/// <remarks>
///     Fluent configuration — <c>modelBuilder.Entity&lt;T&gt;().IsAudited()</c> — is preferred, and
///     wins where the two are configured differently. An attribute puts a persistence concern on a
///     domain type and cannot express a redactor, a key projection, or anything else that needs
///     code. It exists because some models are attribute-configured throughout and consistency
///     within a codebase is worth something.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AuditedAttribute : Attribute
{
    /// <summary>Marks the type for auditing on inserts, updates and deletes.</summary>
    public AuditedAttribute()
        : this(AuditOperations.All)
    {
    }

    /// <summary>Marks the type for auditing on the given operations.</summary>
    /// <param name="operations">Which operations produce an entry.</param>
    public AuditedAttribute(AuditOperations operations)
        => Operations = operations;

    /// <summary>Which operations on this type produce an audit entry.</summary>
    public AuditOperations Operations { get; }
}

/// <summary>
///     Excludes an entity type from auditing.
/// </summary>
/// <remarks>
///     Only meaningful under <c>AuditAllEntities()</c>, where every mapped type is audited by
///     default — but valid either way, so that flipping the model-wide default cannot silently
///     start auditing a type somebody had already said should never be audited.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class NotAuditedAttribute : Attribute;

/// <summary>
///     Leaves a property out of the audit payload entirely.
/// </summary>
/// <remarks>
///     Every property of an audited type is captured; this is the only way a property is not, and
///     it is deliberately per-property. There is no property-level opt-in mode, because the failure
///     that produces — a column added later and silently missing from the trail — is the one an
///     audit log exists to prevent.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class AuditIgnoreAttribute : Attribute;

/// <summary>
///     Records that a property changed without recording its value.
/// </summary>
/// <remarks>
///     The property still appears in the payload's <c>changed</c> list and still has entries under
///     <c>old</c> and <c>new</c> — with the mask token in place of the value. That is the useful
///     middle ground for a secret: the trail shows that somebody rotated the credential and when,
///     without becoming a second copy of it.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class AuditMaskAttribute : Attribute;
