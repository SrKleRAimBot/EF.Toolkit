using System.Linq.Expressions;

namespace EFToolkit.Query.Filtering;

/// <summary>Combines predicate expressions so the result still translates to SQL.</summary>
/// <remarks>
///     <c>Func</c> composition — <c>x =&gt; left(x) &amp;&amp; right(x)</c> — compiles but leaves an
///     <see cref="Expression.Invoke(Expression, Expression[])" /> node in the tree that EF cannot
///     translate. These splice the bodies instead, rebinding the right-hand lambda's parameter onto
///     the left-hand one so the result is the tree the caller would have written by hand.
/// </remarks>
public static class Predicates
{
    /// <summary>A predicate matching every row.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The predicate.</returns>
    public static Expression<Func<T, bool>> True<T>() => static _ => true;

    /// <summary>A predicate matching no rows.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <returns>The predicate.</returns>
    public static Expression<Func<T, bool>> False<T>() => static _ => false;

    /// <summary>Requires both predicates.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="left">The first predicate.</param>
    /// <param name="right">The second predicate.</param>
    /// <returns>A predicate matching rows that satisfy both.</returns>
    public static Expression<Func<T, bool>> And<T>(
        this Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.AndAlso);

    /// <summary>Requires either predicate.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="left">The first predicate.</param>
    /// <param name="right">The second predicate.</param>
    /// <returns>A predicate matching rows that satisfy either.</returns>
    public static Expression<Func<T, bool>> Or<T>(
        this Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.OrElse);

    /// <summary>Negates a predicate.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="predicate">The predicate to negate.</param>
    /// <returns>A predicate matching rows the original did not.</returns>
    /// <remarks>
    ///     Negation over a nullable column follows SQL's three-valued logic, not C#'s: a row whose
    ///     column is <c>NULL</c> satisfies neither the predicate nor its negation.
    /// </remarks>
    public static Expression<Func<T, bool>> Not<T>(this Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Expression.Lambda<Func<T, bool>>(
            Expression.Not(predicate.Body),
            predicate.Parameters[0]);
    }

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> join)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var parameter = left.Parameters[0];
        var rebound = ParameterRebinder.Rebind(right.Body, right.Parameters[0], parameter);

        return Expression.Lambda<Func<T, bool>>(join(left.Body, rebound), parameter);
    }
}
