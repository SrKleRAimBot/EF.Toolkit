using System.Reflection;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;

namespace EFToolkit.Audit.Infrastructure;

/// <summary>
///     Reads auditing attributes into the model, and refuses a model that configures the same thing
///     two incompatible ways.
/// </summary>
/// <remarks>
///     <para>
///         Fluent configuration wins over attributes — but only where they say different things
///         about different concerns. Where they genuinely contradict each other, the model does not
///         build. Comparable libraries resolve such a conflict silently by precedence, and the
///         resulting "why is this property not in the trail" is answerable only by reading their
///         documentation on precedence rules. A startup failure naming both sides is cheaper.
///     </para>
///     <para>
///         Runs at model finalizing, so everything the application configured is already in place
///         and the checks see the model as it will actually be used.
///     </para>
/// </remarks>
internal sealed class AuditModelConvention(AuditOptions options) : IModelFinalizingConvention
{
    /// <inheritdoc />
    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            ApplyAttributes(entityType);
            Validate(entityType);
        }
    }

    private static void ApplyAttributes(IConventionEntityType entityType)
    {
        var clrType = entityType.ClrType;

        if (clrType.GetCustomAttribute<NotAuditedAttribute>(inherit: false) is not null)
        {
            entityType.SetAnnotation(AuditAnnotations.AuditedByAttribute, false);
        }
        else if (clrType.GetCustomAttribute<AuditedAttribute>(inherit: false) is { } audited)
        {
            entityType.SetAnnotation(AuditAnnotations.AuditedByAttribute, true);

            // Only when the attribute narrows the operations, so that stating [Audited] alongside
            // fluent Operations(...) does not quietly widen it back.
            if (audited.Operations != AuditOperations.All
                && entityType.FindAnnotation(AuditAnnotations.Operations) is null)
            {
                entityType.SetAnnotation(AuditAnnotations.Operations, audited.Operations);
            }
        }

        ApplyPropertyAttributes(entityType);
    }

    /// <summary>
    ///     Marks the properties carrying <c>[AuditIgnore]</c> or <c>[AuditMask]</c>, including those
    ///     declared on a complex type.
    /// </summary>
    /// <remarks>
    ///     A complex type's members are where the attribute naturally sits — a <c>Card</c> value
    ///     object declares the number that must be masked, and every entity using it should inherit
    ///     that without restating it. Walking only the declaring entity's own properties meant an
    ///     attribute on a value object was read by nobody and the value went into the trail in
    ///     clear.
    /// </remarks>
    private static void ApplyPropertyAttributes(IConventionTypeBase declaring)
    {
        foreach (var property in declaring.GetDeclaredProperties())
        {
            var member = (MemberInfo?)property.PropertyInfo ?? property.FieldInfo;

            if (member is null)
            {
                continue;
            }

            if (member.GetCustomAttribute<AuditIgnoreAttribute>() is not null)
            {
                property.SetAnnotation(AuditAnnotations.IgnoredByAttribute, true);
            }

            if (member.GetCustomAttribute<AuditMaskAttribute>() is not null)
            {
                property.SetAnnotation(AuditAnnotations.MaskedByAttribute, true);
            }
        }

        foreach (var complex in declaring.GetDeclaredComplexProperties())
        {
            ApplyPropertyAttributes(complex.ComplexType);
        }
    }

    private void Validate(IConventionEntityType entityType)
    {
        var fluent = entityType.FindAnnotation(AuditAnnotations.Audited)?.Value as bool?;
        var attribute = entityType.FindAnnotation(AuditAnnotations.AuditedByAttribute)?.Value as bool?;

        if (fluent is not null && attribute is not null && fluent != attribute)
        {
            throw new AuditNotSupportedException(
                $"'{entityType.DisplayName()}' is configured as "
                + $"{(fluent.Value ? "IsAudited()" : "IsNotAudited()")} and also carries "
                + $"[{(attribute.Value ? nameof(AuditedAttribute) : nameof(NotAuditedAttribute))}]. "
                + "The two disagree, so which one holds would be a matter of precedence rather than "
                + "of intent. Remove one.");
        }

        if (fluent == true && entityType.FindPrimaryKey() is null)
        {
            throw new AuditNotSupportedException(
                $"'{entityType.DisplayName()}' is registered for auditing and has no primary key, "
                + "so its audit entries would identify no row. Give it a key, or remove the "
                + "registration.");
        }

        var excluded = entityType.FindAnnotation(AuditAnnotations.ExcludedProperties)?.Value
            as IReadOnlyList<string> ?? [];

        var masked = entityType.FindAnnotation(AuditAnnotations.MaskedProperties)?.Value
            as IReadOnlyDictionary<string, Func<object?, object?>?>
            ?? new Dictionary<string, Func<object?, object?>?>();

        var keys = entityType.FindAnnotation(AuditAnnotations.KeyProperties)?.Value
            as IReadOnlyList<string> ?? [];

        foreach (var name in excluded.Concat(masked.Keys).Concat(keys))
        {
            if (entityType.FindProperty(name) is null)
            {
                throw new AuditNotSupportedException(
                    $"Auditing for '{entityType.DisplayName()}' names '{name}', which is not a "
                    + "mapped property. A typo here silently stops excluding or masking exactly "
                    + "what it was meant to, so it is refused rather than ignored.");
            }
        }

        foreach (var name in masked.Keys)
        {
            if (entityType.FindProperty(name)?.FindAnnotation(AuditAnnotations.IgnoredByAttribute)
                    ?.Value is true)
            {
                throw new AuditNotSupportedException(
                    $"'{entityType.DisplayName()}.{name}' is masked fluently and carries "
                    + $"[{nameof(AuditIgnoreAttribute)}]. Masking records that it changed while "
                    + "ignoring it records nothing at all, so the two cannot both hold. Remove one.");
            }
        }

        foreach (var name in excluded)
        {
            if (entityType.FindProperty(name)?.FindAnnotation(AuditAnnotations.MaskedByAttribute)
                    ?.Value is true)
            {
                throw new AuditNotSupportedException(
                    $"'{entityType.DisplayName()}.{name}' is excluded fluently and carries "
                    + $"[{nameof(AuditMaskAttribute)}]. Remove one.");
            }
        }

        _ = options;
    }
}

/// <summary>
///     Installs <see cref="AuditModelConvention" /> into the model's convention set.
/// </summary>
/// <remarks>
///     The documented extension point for adding a convention from an
///     <see cref="Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsExtension" />, and
///     the reason attribute-based registration needs no call in <c>OnModelCreating</c>.
/// </remarks>
internal sealed class AuditConventionSetPlugin(AuditOptions options) : IConventionSetPlugin
{
    /// <inheritdoc />
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.Add(new AuditModelConvention(options));
        return conventionSet;
    }
}
