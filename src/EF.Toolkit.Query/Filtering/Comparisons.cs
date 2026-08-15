using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Query.Filtering;

/// <summary>How one column compares against a value, for key types that spell it different ways.</summary>
/// <remarks>
///     Numerics, dates, times and enums define the comparison operators. <c>string</c> and
///     <c>Guid</c> do not, but both implement <see cref="IComparable{T}" />, which EF translates back
///     into a plain SQL comparison against the column. That the resulting order may not be .NET's own
///     — <c>uniqueidentifier</c> famously is not — does not matter here: both sides of the comparison
///     and the <c>ORDER BY</c> are evaluated by the same database, so they agree with each other.
/// </remarks>
internal static class Comparisons
{
    internal static BinaryExpression GreaterThan(Expression left, Expression right)
        => Build(left, right, Expression.GreaterThan, orEqual: false, greater: true);

    internal static BinaryExpression LessThan(Expression left, Expression right)
        => Build(left, right, Expression.LessThan, orEqual: false, greater: false);

    internal static BinaryExpression GreaterThanOrEqual(Expression left, Expression right)
        => Build(left, right, Expression.GreaterThanOrEqual, orEqual: true, greater: true);

    internal static BinaryExpression LessThanOrEqual(Expression left, Expression right)
        => Build(left, right, Expression.LessThanOrEqual, orEqual: true, greater: false);

    internal static BinaryExpression Equal(Expression left, Expression right)
        => Expression.Equal(left, right);

    private static BinaryExpression Build(
        Expression left,
        Expression right,
        Func<Expression, Expression, BinaryExpression> withOperator,
        bool orEqual,
        bool greater)
    {
        // An enum has an ordering but no comparison operators of its own, and Expression.LessThan
        // refuses to build against one. Comparing the underlying numbers says the same thing, and is
        // what EF would have emitted for a hand-written comparison anyway.
        if (NumericFormOf(left.Type) is { } numeric)
        {
            left = Expression.Convert(left, numeric);
            right = Expression.Convert(right, numeric);
        }

        if (HasComparisonOperators(left.Type))
        {
            return withOperator(left, right);
        }

        var compareTo = left.Type.GetMethod(
            nameof(IComparable.CompareTo),
            BindingFlags.Public | BindingFlags.Instance,
            [left.Type]);

        if (compareTo is not null && compareTo.ReturnType == typeof(int))
        {
            var comparison = Expression.Call(left, compareTo, right);
            var zero = Expression.Constant(0);

            return (greater, orEqual) switch
            {
                (true, false) => Expression.GreaterThan(comparison, zero),
                (true, true) => Expression.GreaterThanOrEqual(comparison, zero),
                (false, false) => Expression.LessThan(comparison, zero),
                (false, true) => Expression.LessThanOrEqual(comparison, zero),
            };
        }

        throw new QueryNotSupportedException(
            $"{left.Type.Name} cannot be ordered or range-filtered: it defines neither the comparison "
            + "operators nor IComparable<T>, so there is no way to express \"before\" and \"after\" for "
            + "it. Compare a column of a primitive, string, Guid, date or time type.");
    }

    /// <summary>
    ///     The numeric type an enum is stored as, preserving nullability, or <see langword="null" />
    ///     when the type is not an enum.
    /// </summary>
    private static Type? NumericFormOf(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        var underlying = nullable ?? type;

        if (!underlying.IsEnum)
        {
            return null;
        }

        var numeric = Enum.GetUnderlyingType(underlying);
        return nullable is null ? numeric : typeof(Nullable<>).MakeGenericType(numeric);
    }

    private static bool HasComparisonOperators(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying.IsPrimitive || underlying.IsEnum)
        {
            return true;
        }

        return underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeOnly)
            || underlying == typeof(TimeSpan)
            || underlying.GetMethod("op_GreaterThan", BindingFlags.Public | BindingFlags.Static) is not null;
    }
}
