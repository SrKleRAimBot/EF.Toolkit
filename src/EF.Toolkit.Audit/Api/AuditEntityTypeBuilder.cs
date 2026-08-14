using System.Linq.Expressions;
using EFToolkit.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Api;

/// <summary>
///     Configures how one entity type is audited.
/// </summary>
/// <typeparam name="TEntity">The entity type being configured.</typeparam>
/// <remarks>
///     <para>
///         <strong>Every mapped property of an audited type is captured.</strong> This builder
///         narrows that — it never widens it — so a property added to the type later is audited
///         from the moment it exists, with no configuration change. There is no property-level
///         opt-in, because the failure that allows is precisely the one an audit trail exists to
///         prevent.
///     </para>
///     <para>
///         Every method is <see langword="virtual" /> so a provider package can extend the surface
///         without the base having to know about it, matching how EF.Toolkit.Bulk's options builders
///         are extended.
///     </para>
/// </remarks>
public class AuditEntityTypeBuilder<TEntity>
    where TEntity : class
{
    /// <summary>Initializes a new instance over <paramref name="entityType" />.</summary>
    /// <param name="entityType">The entity type being configured.</param>
    public AuditEntityTypeBuilder(IMutableEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        EntityType = entityType;
    }

    /// <summary>The entity type this builder writes its configuration onto.</summary>
    protected IMutableEntityType EntityType { get; }

    /// <summary>Restricts which operations on this type produce an audit entry.</summary>
    /// <param name="operations">The operations to audit. Defaults to all three.</param>
    /// <remarks>
    ///     Narrowing this is a real decision, not a tuning knob: an entity audited on insert and
    ///     update but not delete leaves no record that a row ever existed.
    /// </remarks>
    public virtual AuditEntityTypeBuilder<TEntity> Operations(AuditOperations operations)
    {
        if (operations == AuditOperations.None)
        {
            throw new AuditNotSupportedException(
                $"'{typeof(TEntity).Name}' was registered for auditing with no operations. Use "
                + "IsNotAudited() to say the type is not audited, rather than auditing it for "
                + "nothing.");
        }

        EntityType.SetAnnotation(AuditAnnotations.Operations, operations);
        return this;
    }

    /// <summary>Leaves one or more properties out of the audit payload entirely.</summary>
    /// <param name="properties">
    ///     <c>o =&gt; o.InternalNotes</c>, or <c>o =&gt; new { o.DraftJson, o.ScratchPad }</c>.
    /// </param>
    /// <remarks>
    ///     Use this for noise. For a secret, prefer <see cref="Mask(Expression{Func{TEntity, object}})" />:
    ///     an excluded property leaves no trace that it changed at all, while a masked one records
    ///     the change without recording the value.
    /// </remarks>
    public virtual AuditEntityTypeBuilder<TEntity> Exclude(
        Expression<Func<TEntity, object?>> properties)
    {
        var names = AuditPropertySelector.Resolve(properties, "Exclude");
        var excluded = new List<string>(Excluded());

        foreach (var name in names)
        {
            if (!excluded.Contains(name, StringComparer.Ordinal))
            {
                excluded.Add(name);
            }
        }

        EntityType.SetAnnotation(AuditAnnotations.ExcludedProperties, (IReadOnlyList<string>)excluded);
        return this;
    }

    /// <summary>Records that a property changed, without recording its value.</summary>
    /// <param name="properties">
    ///     <c>o =&gt; o.CardNumber</c>, or <c>o =&gt; new { o.CardNumber, o.Cvv }</c>.
    /// </param>
    public virtual AuditEntityTypeBuilder<TEntity> Mask(
        Expression<Func<TEntity, object?>> properties)
        => Mask(properties, redactor: null);

    /// <summary>Records a property's value through <paramref name="redactor" />.</summary>
    /// <param name="properties">The properties to redact.</param>
    /// <param name="redactor">
    ///     Turns the property's provider value into what should be recorded — <c>v =&gt; Last4(v)</c>.
    ///     Receives <see langword="null" /> for a null value and must tolerate it.
    /// </param>
    /// <remarks>
    ///     A redactor is a delegate held on a model annotation, so an entity type configured this
    ///     way cannot be part of a compiled model. Use the token form where that matters.
    /// </remarks>
    public virtual AuditEntityTypeBuilder<TEntity> Mask(
        Expression<Func<TEntity, object?>> properties,
        Func<object?, object?>? redactor)
    {
        var names = AuditPropertySelector.Resolve(properties, "Mask");
        var masked = new Dictionary<string, Func<object?, object?>?>(Masked(), StringComparer.Ordinal);

        foreach (var name in names)
        {
            masked[name] = redactor;
        }

        EntityType.SetAnnotation(
            AuditAnnotations.MaskedProperties,
            (IReadOnlyDictionary<string, Func<object?, object?>?>)masked);

        return this;
    }

    /// <summary>Builds the entry's <c>EntityKey</c> from something other than the primary key.</summary>
    /// <param name="properties">The properties to build the key from.</param>
    /// <remarks>
    ///     For a type whose primary key is an internal surrogate and whose meaningful identity is a
    ///     public code. The primary key is still written into the payload's <c>key</c> object, so
    ///     nothing is lost — this only changes what the indexed column holds.
    /// </remarks>
    public virtual AuditEntityTypeBuilder<TEntity> KeyFrom(
        Expression<Func<TEntity, object?>> properties)
    {
        var names = AuditPropertySelector.Resolve(properties, "KeyFrom");
        EntityType.SetAnnotation(AuditAnnotations.KeyProperties, names);
        return this;
    }

    private IReadOnlyList<string> Excluded()
        => EntityType.FindAnnotation(AuditAnnotations.ExcludedProperties)?.Value
            as IReadOnlyList<string> ?? [];

    private IReadOnlyDictionary<string, Func<object?, object?>?> Masked()
        => EntityType.FindAnnotation(AuditAnnotations.MaskedProperties)?.Value
            as IReadOnlyDictionary<string, Func<object?, object?>?>
            ?? new Dictionary<string, Func<object?, object?>?>(StringComparer.Ordinal);
}
