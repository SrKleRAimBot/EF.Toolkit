using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFBulk.Api;

/// <summary>
///     How to walk an entity type's navigations, and how to copy principal keys into the foreign
///     keys that reference them.
/// </summary>
/// <remarks>
///     <para>
///         Change tracking normally does this fixup: assign <c>order.Customer</c> and EF sets
///         <c>order.CustomerId</c> for you once the customer has a key. The explicit API has no
///         tracker, so with <c>IncludeGraph</c> it has to perform the same copy explicitly — after
///         the principal has been written and its generated key is known.
///     </para>
///     <para>
///         Accessors are compiled once per entity type and cached against the model, for the same
///         reason as <see cref="BulkEntityPlan" />: per-row reflection would cost more than the
///         pipeline this API exists to avoid.
///     </para>
/// </remarks>
internal sealed class EntityGraphPlan
{
    private static readonly ConcurrentDictionary<IEntityType, EntityGraphPlan> Cache = new();

    private EntityGraphPlan(
        IReadOnlyList<NavigationAccessor> navigations,
        IReadOnlyList<ForeignKeyFixup> fixups)
    {
        Navigations = navigations;
        Fixups = fixups;
    }

    /// <summary>Navigations to follow when collecting the graph.</summary>
    public IReadOnlyList<NavigationAccessor> Navigations { get; }

    /// <summary>Foreign keys on this type that can be filled from a navigation.</summary>
    public IReadOnlyList<ForeignKeyFixup> Fixups { get; }

    public static EntityGraphPlan For(IEntityType entityType)
        => Cache.GetOrAdd(entityType, static type => Build(type));

    private static EntityGraphPlan Build(IEntityType entityType)
    {
        var navigations = new List<NavigationAccessor>();
        var fixups = new List<ForeignKeyFixup>();

        foreach (var navigation in entityType.GetNavigations())
        {
            if (navigation.PropertyInfo is null && navigation.FieldInfo is null)
            {
                continue;
            }

            var getter = CompileGetter(entityType, navigation);
            navigations.Add(new NavigationAccessor(
                navigation.TargetEntityType, getter, navigation.IsCollection));

            // Only the dependent side can be fixed up: it owns the foreign key columns.
            if (navigation.IsCollection || !navigation.IsOnDependent)
            {
                continue;
            }

            var foreignKey = navigation.ForeignKey;
            var setters = new List<Action<object, object?>>();
            var principalGetters = new List<Func<object, object?>>();
            var usable = true;

            for (var i = 0; i < foreignKey.Properties.Count; i++)
            {
                var setter = CompileSetter(entityType, foreignKey.Properties[i]);
                var principalGetter = CompilePropertyGetter(
                    foreignKey.PrincipalEntityType, foreignKey.PrincipalKey.Properties[i]);

                if (setter is null || principalGetter is null)
                {
                    usable = false;
                    break;
                }

                setters.Add(setter);
                principalGetters.Add(principalGetter);
            }

            if (usable)
            {
                fixups.Add(new ForeignKeyFixup(
                    foreignKey.PrincipalEntityType, getter, [.. principalGetters], [.. setters]));
            }
        }

        return new EntityGraphPlan(navigations, fixups);
    }

    private static Func<object, object?> CompileGetter(IEntityType entityType, INavigation navigation)
    {
        var parameter = Expression.Parameter(typeof(object), "entity");
        var typed = Expression.Convert(parameter, entityType.ClrType);

        Expression access = navigation.PropertyInfo is not null
            ? Expression.Property(typed, navigation.PropertyInfo)
            : Expression.Field(typed, navigation.FieldInfo!);

        return Expression
            .Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), parameter)
            .Compile();
    }

    private static Func<object, object?>? CompilePropertyGetter(IEntityType entityType, IProperty property)
    {
        if (property.PropertyInfo is null && property.FieldInfo is null)
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(object), "entity");
        var typed = Expression.Convert(parameter, entityType.ClrType);

        Expression access = property.PropertyInfo is not null
            ? Expression.Property(typed, property.PropertyInfo)
            : Expression.Field(typed, property.FieldInfo!);

        return Expression
            .Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), parameter)
            .Compile();
    }

    private static Action<object, object?>? CompileSetter(IEntityType entityType, IProperty property)
    {
        var writableField = property.FieldInfo is { IsInitOnly: false };
        if (!writableField && property.PropertyInfo is not { CanWrite: true })
        {
            return null;
        }

        var entity = Expression.Parameter(typeof(object), "entity");
        var value = Expression.Parameter(typeof(object), "value");
        var typed = Expression.Convert(entity, entityType.ClrType);

        var target = writableField
            ? Expression.Field(typed, property.FieldInfo!)
            : (MemberExpression)Expression.Property(typed, property.PropertyInfo!);

        var assign = Expression.Assign(target, Expression.Convert(value, target.Type));
        return Expression.Lambda<Action<object, object?>>(assign, entity, value).Compile();
    }
}

/// <summary>A navigation, with a compiled accessor for reading it.</summary>
/// <param name="Target">The entity type on the other end.</param>
/// <param name="Get">Reads the navigation's value.</param>
/// <param name="IsCollection">Whether the value is a collection.</param>
internal sealed record NavigationAccessor(
    IEntityType Target,
    Func<object, object?> Get,
    bool IsCollection)
{
    /// <summary>Yields the related entities reachable through this navigation.</summary>
    public IEnumerable<object> Read(object entity)
    {
        var value = Get(entity);
        if (value is null)
        {
            yield break;
        }

        if (!IsCollection)
        {
            yield return value;
            yield break;
        }

        foreach (var item in (IEnumerable)value)
        {
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}

/// <summary>Copies a principal's key into the foreign key that references it.</summary>
/// <param name="PrincipalType">
///     The entity type on the principal end. Used to tell a self-reference, which constrains the
///     order of rows within one table, from an ordinary foreign key, which the table-level sort has
///     already handled.
/// </param>
/// <param name="GetPrincipal">Reads the reference navigation on the dependent.</param>
/// <param name="PrincipalKey">Reads the principal's key values.</param>
/// <param name="SetForeignKey">Writes the dependent's foreign key values.</param>
internal sealed record ForeignKeyFixup(
    IEntityType PrincipalType,
    Func<object, object?> GetPrincipal,
    Func<object, object?>[] PrincipalKey,
    Action<object, object?>[] SetForeignKey)
{
    /// <summary>
    ///     Applies the fixup to <paramref name="dependent" />, if it has a principal to read from.
    /// </summary>
    /// <remarks>
    ///     A null navigation is left alone: either the foreign key is optional and genuinely unset,
    ///     or the caller already assigned it directly.
    /// </remarks>
    public void Apply(object dependent)
    {
        var principal = GetPrincipal(dependent);
        if (principal is null)
        {
            return;
        }

        for (var i = 0; i < SetForeignKey.Length; i++)
        {
            SetForeignKey[i](dependent, PrincipalKey[i](principal));
        }
    }
}
