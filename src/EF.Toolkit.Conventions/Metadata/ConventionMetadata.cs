using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Conventions.Metadata;

/// <summary>How a soft-delete property records that a row is deleted.</summary>
internal enum SoftDeleteKind
{
    /// <summary>A <see cref="bool" />: <see langword="true" /> means deleted.</summary>
    Flag,

    /// <summary>A nullable <see cref="DateTime" /> or <see cref="DateTimeOffset" />: set means deleted.</summary>
    Timestamp,
}

/// <summary>The soft-delete property of an entity type, read back from the model.</summary>
/// <param name="Property">The property that marks a row deleted.</param>
/// <param name="Kind">How it marks it.</param>
internal sealed record SoftDeleteMapping(IProperty Property, SoftDeleteKind Kind);

/// <summary>The timestamp properties of an entity type, read back from the model.</summary>
/// <param name="CreatedAt">Set once, when the row is inserted. <see langword="null" /> if not configured.</param>
/// <param name="UpdatedAt">Set on every write. <see langword="null" /> if not configured.</param>
internal sealed record TimestampMapping(IProperty? CreatedAt, IProperty? UpdatedAt);

/// <summary>
///     The annotations EF.Toolkit.Conventions stores on the model, and the code that reads them back.
/// </summary>
/// <remarks>
///     Configuration lives in the model rather than in options because it is per entity type, and the
///     model is the one thing both the save interceptor and the query helpers can reach from any
///     context. Annotations hold property <em>names</em>: a name survives model finalization, where a
///     builder-time property object does not.
/// </remarks>
internal static class ConventionMetadata
{
    internal const string SoftDeleteAnnotation = "EFToolkit:SoftDelete";
    internal const string CreatedAtAnnotation = "EFToolkit:CreatedAt";
    internal const string UpdatedAtAnnotation = "EFToolkit:UpdatedAt";

    /// <summary>The soft-delete mapping of <paramref name="entityType" />, if it has one.</summary>
    /// <remarks>
    ///     Read from the root type: EF puts query filters on hierarchy roots only, so a derived type is
    ///     soft-deletable exactly when its root is.
    /// </remarks>
    internal static SoftDeleteMapping? FindSoftDelete(IEntityType entityType)
    {
        var root = entityType.GetRootType();
        if (root[SoftDeleteAnnotation] is not string name)
        {
            return null;
        }

        var property = entityType.FindProperty(name)
            ?? throw new InvalidOperationException(
                $"The soft-delete property '{name}' configured on '{root.DisplayName()}' is not in the model.");

        return new SoftDeleteMapping(property, KindOf(property.ClrType, name, root.DisplayName()));
    }

    /// <summary>The timestamp mapping of <paramref name="entityType" />, if it has either timestamp.</summary>
    internal static TimestampMapping? FindTimestamps(IEntityType entityType)
    {
        var created = Find(entityType, CreatedAtAnnotation);
        var updated = Find(entityType, UpdatedAtAnnotation);
        return created is null && updated is null ? null : new TimestampMapping(created, updated);

        static IProperty? Find(IEntityType entityType, string annotation)
        {
            for (var type = entityType; type is not null; type = type.BaseType)
            {
                if (type[annotation] is string name)
                {
                    return entityType.FindProperty(name);
                }
            }

            return null;
        }
    }

    /// <summary>The value a soft-delete property takes when the row is deleted.</summary>
    internal static object DeletedValue(SoftDeleteMapping mapping, DateTimeOffset now)
        => mapping.Kind == SoftDeleteKind.Flag ? true : TimestampValue(mapping.Property.ClrType, now);

    /// <summary>The value a soft-delete property takes when the row is not deleted.</summary>
    internal static object? LiveValue(SoftDeleteMapping mapping)
        => mapping.Kind == SoftDeleteKind.Flag ? false : null;

    /// <summary><paramref name="now" /> as a value of the property's CLR type.</summary>
    /// <remarks>
    ///     A <see cref="DateTime" /> is always UTC. A local time in a column is ambiguous for an hour a
    ///     year, and there is no column type that remembers which zone wrote it.
    /// </remarks>
    internal static object TimestampValue(Type clrType, DateTimeOffset now)
        => (Nullable.GetUnderlyingType(clrType) ?? clrType) == typeof(DateTimeOffset)
            ? (object)now
            // Boxed separately: as a conditional's two arms, DateTime would convert straight back
            // to DateTimeOffset.
            : (object)now.UtcDateTime;

    /// <summary>Whether <paramref name="type" /> is a type a timestamp property may have.</summary>
    internal static bool IsTimestampType(Type type)
        => (Nullable.GetUnderlyingType(type) ?? type) is var t
            && (t == typeof(DateTime) || t == typeof(DateTimeOffset));

    /// <summary><c>e =&gt; !e.Flag</c> or <c>e =&gt; e.DeletedAt == null</c>, over <c>EF.Property</c>.</summary>
    /// <remarks>
    ///     Built over <c>EF.Property</c> rather than the CLR member so that one shape serves both mapped
    ///     and shadow properties, and the query filter, the set-based writes and <c>OnlyDeleted</c>
    ///     all agree on what "deleted" means.
    /// </remarks>
    internal static Expression<Func<TEntity, bool>> IsDeleted<TEntity>(string propertyName, Type clrType)
    {
        var row = Expression.Parameter(typeof(TEntity), "e");
        var value = PropertyAccess(row, propertyName, clrType);

        Expression body = clrType == typeof(bool)
            ? value
            : Expression.NotEqual(value, Expression.Constant(null, clrType));

        return Expression.Lambda<Func<TEntity, bool>>(body, row);
    }

    /// <summary>The negation of <see cref="IsDeleted{TEntity}" />.</summary>
    internal static Expression<Func<TEntity, bool>> IsLive<TEntity>(string propertyName, Type clrType)
    {
        var deleted = IsDeleted<TEntity>(propertyName, clrType);
        return Expression.Lambda<Func<TEntity, bool>>(Expression.Not(deleted.Body), deleted.Parameters);
    }

    /// <summary><c>EF.Property&lt;T&gt;(row, name)</c>.</summary>
    internal static MethodCallExpression PropertyAccess(Expression row, string propertyName, Type clrType)
        => Expression.Call(
            typeof(EF), nameof(EF.Property), [clrType], row, Expression.Constant(propertyName));

    internal static SoftDeleteKind KindOf(Type clrType, string name, string entity)
    {
        if (clrType == typeof(bool))
        {
            return SoftDeleteKind.Flag;
        }

        if (Nullable.GetUnderlyingType(clrType) is not null && IsTimestampType(clrType))
        {
            return SoftDeleteKind.Timestamp;
        }

        throw new InvalidOperationException(
            $"The soft-delete property '{entity}.{name}' is a '{clrType.Name}'. It must be a bool (true "
            + "means deleted) or a nullable DateTime or DateTimeOffset (a value means deleted).");
    }
}
