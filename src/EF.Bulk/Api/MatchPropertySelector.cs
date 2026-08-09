using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFBulk.Api;

/// <summary>
///     Turns a <c>MatchOn</c> lambda into the properties a merge should match on.
/// </summary>
/// <remarks>
///     Accepts a single property (<c>c =&gt; c.Email</c>) or an anonymous type for a composite match
///     (<c>c =&gt; new { c.TenantId, c.Email }</c>), mirroring how EF itself accepts key and index
///     selectors.
/// </remarks>
internal static class MatchPropertySelector
{
    public static IReadOnlyList<IProperty> Resolve<TEntity>(
        IEntityType entityType,
        Expression<Func<TEntity, object?>> selector)
    {
        var names = MemberNames(selector.Body, selector.Parameters[0]);

        if (names.Count == 0)
        {
            throw new BulkNotSupportedException(
                "MatchOn must select one or more properties, for example "
                + "'c => c.Email' or 'c => new { c.TenantId, c.Email }'.");
        }

        var properties = new List<IProperty>(names.Count);
        foreach (var name in names)
        {
            properties.Add(
                entityType.FindProperty(name)
                ?? throw new BulkNotSupportedException(
                    $"'{entityType.DisplayName()}.{name}' is not a mapped property, so it cannot "
                    + "be matched on."));
        }

        return properties;
    }

    private static List<string> MemberNames(Expression body, ParameterExpression parameter)
    {
        // A value-typed property picked through Func<T, object?> arrives wrapped in a boxing
        // conversion, which carries no information and is unwrapped here.
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }

        switch (body)
        {
            case MemberExpression member when member.Expression == parameter:
                return [member.Member.Name];

            case NewExpression anonymous:
                var names = new List<string>(anonymous.Arguments.Count);
                foreach (var argument in anonymous.Arguments)
                {
                    var unwrapped = argument is UnaryExpression { NodeType: ExpressionType.Convert } c
                        ? c.Operand
                        : argument;

                    if (unwrapped is not MemberExpression { Member: PropertyInfo or FieldInfo } m
                        || m.Expression != parameter)
                    {
                        throw new BulkNotSupportedException(
                            "MatchOn may only reference properties of the entity directly, for "
                            + "example 'c => new { c.TenantId, c.Email }'.");
                    }

                    names.Add(m.Member.Name);
                }

                return names;

            default:
                throw new BulkNotSupportedException(
                    "MatchOn must be a property access or an anonymous type of property accesses, "
                    + "for example 'c => c.Email' or 'c => new { c.TenantId, c.Email }'.");
        }
    }
}
