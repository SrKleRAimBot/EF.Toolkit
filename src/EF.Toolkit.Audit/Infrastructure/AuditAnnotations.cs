namespace EFToolkit.Audit.Infrastructure;

/// <summary>
///     Names for the annotations this library hangs off EF's metadata objects.
/// </summary>
/// <remarks>
///     <para>
///         Two kinds live here. Model annotations carry configuration from <c>OnModelCreating</c>
///         to where it is read, and are part of the model's identity. Runtime annotations cache what
///         is derived from a finalized model — the per-entity plan, compiled accessors — and follow
///         the reasoning EF.Toolkit.Bulk's <c>BulkAnnotations</c> sets out: hanging the derived
///         value off the metadata it came from means it is collected with the model, rather than
///         being pinned forever by a static dictionary keyed on it.
///     </para>
///     <para>
///         Fluent and attribute configuration are stored under <em>separate</em> names rather than
///         being merged as they are applied. Keeping them apart is what lets model validation see
///         that the two disagree and say so, instead of silently letting whichever ran last win —
///         the precedence confusion that comparable libraries are best known for.
///     </para>
/// </remarks>
internal static class AuditAnnotations
{
    // Annotations share one namespace per metadata object with EF's own and with every other
    // library's, so the prefix is not decoration.
    private const string Prefix = "EFToolkit.Audit:";

    /// <summary>
    ///     <see langword="true" /> or <see langword="false" /> when the entity type was configured
    ///     fluently, absent when it was not. Set on the <c>IEntityType</c>.
    /// </summary>
    public const string Audited = Prefix + "Audited";

    /// <summary>The same, as stated by an attribute. Set on the <c>IEntityType</c>.</summary>
    public const string AuditedByAttribute = Prefix + "AuditedByAttribute";

    /// <summary>The configured <c>AuditOperations</c>. Set on the <c>IEntityType</c>.</summary>
    public const string Operations = Prefix + "Operations";

    /// <summary>Property names excluded fluently, as <c>IReadOnlyList&lt;string&gt;</c>.</summary>
    public const string ExcludedProperties = Prefix + "ExcludedProperties";

    /// <summary>
    ///     Property names masked fluently, as
    ///     <c>IReadOnlyDictionary&lt;string, Func&lt;object?, object?&gt;?&gt;</c> — a null value
    ///     means the configured mask token, a non-null one a custom redactor.
    /// </summary>
    public const string MaskedProperties = Prefix + "MaskedProperties";

    /// <summary>Property names to build the entry key from, as <c>IReadOnlyList&lt;string&gt;</c>.</summary>
    public const string KeyProperties = Prefix + "KeyProperties";

    /// <summary><see langword="true" /> when <c>[AuditIgnore]</c> was applied. Set on the <c>IProperty</c>.</summary>
    public const string IgnoredByAttribute = Prefix + "IgnoredByAttribute";

    /// <summary><see langword="true" /> when <c>[AuditMask]</c> was applied. Set on the <c>IProperty</c>.</summary>
    public const string MaskedByAttribute = Prefix + "MaskedByAttribute";

    /// <summary>The resolved capture plan. Runtime annotation on the <c>IEntityType</c>.</summary>
    public const string Plan = Prefix + "Plan";

    /// <summary>A compiled getter for a property. Runtime annotation on the <c>IProperty</c>.</summary>
    public const string Getter = Prefix + "Getter";
}
