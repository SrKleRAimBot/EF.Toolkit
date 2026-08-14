using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Audit.Api;

/// <summary>
///     Turns a property-selecting lambda into the property names it mentions.
/// </summary>
/// <remarks>
///     <para>
///         Accepts a single property (<c>o =&gt; o.CardNumber</c>) or an anonymous type for several
///         (<c>o =&gt; new { o.DraftJson, o.ScratchPad }</c>), the same idiom EF itself uses for key
///         and index selectors and the same one EF.Toolkit.Bulk uses for <c>MatchOn</c>.
///     </para>
///     <para>
///         Names, not <c>IProperty</c>s. This runs while <c>OnModelCreating</c> is still building
///         the model, so resolving to metadata here would make the configuration order-sensitive —
///         calling <c>IsAudited</c> before the property was mapped would fail for no good reason.
///         The names are checked against the finalized model instead, where a typo is a startup
///         error rather than a silently dropped exclusion.
///     </para>
/// </remarks>
internal static class AuditPropertySelector
{
    public static IReadOnlyList<string> Resolve<TEntity>(
        Expression<Func<TEntity, object?>> selector,
        string call)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var names = MemberNames(selector.Body, selector.Parameters[0], call);

        if (names.Count == 0)
        {
            throw new AuditNotSupportedException(
                $"{call} must select one or more properties, for example "
                + "'o => o.CardNumber' or 'o => new { o.DraftJson, o.ScratchPad }'.");
        }

        return names;
    }

    private static List<string> MemberNames(
        Expression body,
        ParameterExpression parameter,
        string call)
    {
        // A value-typed property picked through Func<T, object?> arrives wrapped in a boxing
        // conversion, which carries no information and is unwrapped here.
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert)
        {
            body = convert.Operand;
        }

        switch (body)
        {
            case MemberExpression { Member: PropertyInfo or FieldInfo } member
                when member.Expression == parameter:
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
                        throw new AuditNotSupportedException(
                            $"{call} may only reference properties of the entity directly, for "
                            + "example 'o => new { o.DraftJson, o.ScratchPad }'.");
                    }

                    names.Add(m.Member.Name);
                }

                return names;

            default:
                throw new AuditNotSupportedException(
                    $"{call} must be a property access or an anonymous type of property accesses, "
                    + "for example 'o => o.CardNumber' or 'o => new { o.DraftJson, o.ScratchPad }'.");
        }
    }
}
