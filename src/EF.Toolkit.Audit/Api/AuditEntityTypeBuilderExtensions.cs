using EFToolkit.Audit.Api;
using EFToolkit.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

// Deliberately in EF's own namespace so IsAudited() is visible with the using that any EF Core
// model configuration already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Registers entity types for auditing.
/// </summary>
public static class AuditEntityTypeBuilderExtensions
{
    /// <summary>
    ///     Audits this entity type.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="builder">The type being configured.</param>
    /// <param name="configure">
    ///     Narrows what is captured — which operations, which properties are excluded or masked.
    ///     Omitted, every property is captured on every operation.
    /// </param>
    /// <remarks>
    ///     Valid under either model-wide default. Under the opt-in default this is what registers
    ///     the type; under <c>AuditAllEntities()</c> it is redundant but harmless, and stating it
    ///     means flipping the default later cannot silently stop auditing a type somebody had
    ///     already said should be.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     modelBuilder.Entity&lt;Order&gt;().IsAudited(a => a
    ///         .Exclude(o => o.InternalNotes)
    ///         .Mask(o => o.CardNumber));
    ///     </code>
    /// </example>
    public static EntityTypeBuilder<TEntity> IsAudited<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Action<AuditEntityTypeBuilder<TEntity>>? configure = null)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetAnnotation(AuditAnnotations.Audited, true);
        configure?.Invoke(new AuditEntityTypeBuilder<TEntity>(builder.Metadata));

        return builder;
    }

    /// <summary>
    ///     Never audits this entity type.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="builder">The type being configured.</param>
    /// <remarks>
    ///     Chiefly for <c>AuditAllEntities()</c> models, where this is how a type opts out. It also
    ///     wins over an <c>[Audited]</c> attribute — but only by refusing to build the model and
    ///     saying the two disagree, rather than by quietly overruling it.
    /// </remarks>
    public static EntityTypeBuilder<TEntity> IsNotAudited<TEntity>(
        this EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Metadata.SetAnnotation(AuditAnnotations.Audited, false);
        return builder;
    }
}
