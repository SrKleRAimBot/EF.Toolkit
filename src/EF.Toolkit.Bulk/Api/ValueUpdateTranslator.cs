using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Query;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Turns an object-initializer expression — <c>o =&gt; new Order { Status = s, Total = o.Total * 2 }</c>
///     — into the <c>SetProperty</c> calls EF Core's <c>ExecuteUpdate</c> takes.
/// </summary>
/// <remarks>
///     <para>
///         The initializer is the shape that gives the call site IntelliSense for free: inside
///         <c>new Order { … }</c> the editor already lists exactly the members that can be
///         assigned, and the assigned values are ordinary expressions over the row parameter. EF's
///         own builder form, <c>s =&gt; s.SetProperty(o =&gt; o.Status, s)</c>, repeats the row
///         lambda for every column.
///     </para>
///     <para>
///         An initializer is also a <em>construction</em>, though, and every part of one that is not
///         a plain member assignment would mean something a set-based <c>UPDATE</c> cannot do:
///         constructor arguments, collection initializers, a different type. Each of those is
///         refused by name rather than ignored, because an ignored assignment is a column that
///         silently keeps its old value.
///     </para>
/// </remarks>
internal static class ValueUpdateTranslator
{
    private static readonly ConcurrentDictionary<Type, MethodInfo> SetPropertyMethods = new();

    /// <summary>One column assignment: which member, and the value written to it.</summary>
    /// <param name="Property">
    ///     <c>o =&gt; o.Member</c>, or a chain <c>o =&gt; o.Complex.Member</c> for a complex
    ///     property.
    /// </param>
    /// <param name="Value">The value, as a lambda over the same row parameter.</param>
    internal sealed record Setter(LambdaExpression Property, LambdaExpression Value);

    /// <summary>Validates <paramref name="valueUpdates" /> and splits it into setters.</summary>
    /// <exception cref="ArgumentException">The expression is not a plain object initializer.</exception>
    internal static IReadOnlyList<Setter> Translate<TEntity>(Expression<Func<TEntity, TEntity>> valueUpdates)
    {
        ArgumentNullException.ThrowIfNull(valueUpdates);

        var row = valueUpdates.Parameters[0];
        var body = StripConversions(valueUpdates.Body);

        if (body is NewExpression { Arguments.Count: 0 } bare && bare.Type == typeof(TEntity))
        {
            // 'x => new T()' is an initializer with the braces left off — the right shape, but empty.
            throw Invalid("assigns no members, so there is nothing to update.");
        }

        if (body is not MemberInitExpression init)
        {
            throw Invalid(
                $"must be an object initializer such as 'x => new {typeof(TEntity).Name} {{ Column = value }}', "
                + $"but was '{valueUpdates.Body}'. Each assignment in the initializer names a column to set.");
        }

        if (init.NewExpression.Type != typeof(TEntity))
        {
            // A derived type's initializer reads as "update these rows to be that type", which is a
            // discriminator change no set-based update performs. Filter with OfType<T>() instead.
            throw Invalid(
                $"constructs '{init.NewExpression.Type.Name}', not '{typeof(TEntity).Name}'. To update only "
                + $"rows of a derived type, start from 'OfType<{init.NewExpression.Type.Name}>()'.");
        }

        if (init.NewExpression.Arguments.Count > 0)
        {
            // A constructor argument is not tied to a column the way an assignment is: there is no
            // reliable way to tell which property it ends up in, so it cannot become a SET.
            throw Invalid(
                "passes constructor arguments. Only member assignments inside the braces are translated to "
                + "columns; move each value into the initializer, or use the UpdateSettersBuilder overload.");
        }

        var setters = new List<Setter>();
        CollectBindings(init.Bindings, row, row, setters);

        if (setters.Count == 0)
        {
            throw Invalid("assigns no members, so there is nothing to update.");
        }

        var duplicate = setters
            .GroupBy(s => s.Property.Body.ToString(), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            // The C# compiler already rejects a member assigned twice in one initializer, so this is
            // reachable only through a hand-built tree — but an UPDATE with the column twice is an
            // error on most engines and an arbitrary pick on the rest.
            throw Invalid($"assigns '{duplicate.Key}' more than once.");
        }

        return setters;
    }

    /// <summary>Builds the <c>ExecuteUpdate</c> callback that applies <paramref name="setters" />.</summary>
    internal static Action<UpdateSettersBuilder<TEntity>> ToSetterCallback<TEntity>(IReadOnlyList<Setter> setters)
    {
        var setProperty = SetPropertyMethods.GetOrAdd(typeof(TEntity), static _ => FindSetProperty<TEntity>());

        return builder =>
        {
            foreach (var setter in setters)
            {
                setProperty
                    .MakeGenericMethod(setter.Property.ReturnType)
                    .Invoke(builder, [setter.Property, setter.Value]);
            }
        };
    }

    private static void CollectBindings(
        IEnumerable<MemberBinding> bindings,
        ParameterExpression row,
        Expression target,
        List<Setter> setters)
    {
        foreach (var binding in bindings)
        {
            var member = Expression.MakeMemberAccess(target, binding.Member);

            switch (binding)
            {
                case MemberAssignment assignment:
                {
                    var memberType = member.Type;
                    var value = assignment.Expression.Type == memberType
                        ? assignment.Expression
                        : Expression.Convert(assignment.Expression, memberType);

                    var delegateType = typeof(Func<,>).MakeGenericType(row.Type, memberType);
                    setters.Add(new Setter(
                        Expression.Lambda(delegateType, member, row),
                        Expression.Lambda(delegateType, value, row)));
                    break;
                }

                case MemberMemberBinding nested:
                    // 'Address = { City = c }' assigns into the existing complex value rather than
                    // replacing it, which is exactly a SET on the nested column.
                    CollectBindings(nested.Bindings, row, member, setters);
                    break;

                default:
                    throw Invalid(
                        $"uses a collection initializer on '{binding.Member.Name}'. A set-based update writes "
                        + "columns; it cannot add elements to a collection.");
            }
        }
    }

    private static MethodInfo FindSetProperty<TEntity>()
        => typeof(UpdateSettersBuilder<TEntity>)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m =>
            {
                if (m.Name != nameof(UpdateSettersBuilder<TEntity>.SetProperty) || !m.IsGenericMethodDefinition)
                {
                    return false;
                }

                // The (property selector, value selector) overload: both parameters are expressions.
                var parameters = m.GetParameters();
                return parameters.Length == 2
                    && parameters.All(p => p.ParameterType.IsGenericType
                        && p.ParameterType.GetGenericTypeDefinition() == typeof(Expression<>));
            });

    private static Expression StripConversions(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        return expression;
    }

    // Named for the public parameter the caller wrote, which is not a parameter of this helper.
#pragma warning disable CA2208
    private static ArgumentException Invalid(string reason)
        => new($"The value updates expression {reason}", "valueUpdates");
#pragma warning restore CA2208
}
