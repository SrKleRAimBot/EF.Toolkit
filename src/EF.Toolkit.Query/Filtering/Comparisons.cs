using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Query.Filtering;

/// <summary>How one column compares against a value, for key types that spell it different ways.</summary>
/// <remarks>
///     <para>
///         Numerics, dates, times and enums define the comparison operators. <c>string</c> and
///         <c>Guid</c> do not, but both implement <see cref="IComparable{T}" />, which EF translates
///         back into a plain SQL comparison against the column. A type that spells out no ordering at
///         all — a strongly typed id over <c>text</c>, say — is compared as the database orders the
///         column it is stored in.
///     </para>
///     <para>
///         That the resulting order may not be .NET's own — <c>uniqueidentifier</c> famously is not —
///         does not matter here: both sides of the comparison and the <c>ORDER BY</c> are evaluated by
///         the same database, so they agree with each other.
///     </para>
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
        => HasEqualityOperators(left.Type)
            ? Expression.Equal(left, right)
            : Expression.Equal(left, right, liftToNull: false, MethodOf(nameof(StoreOrder.Same), left.Type));

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

        if (CompareToOf(left.Type) is { } compareTo)
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

        return AsTheStoreOrdersIt(left, right, orEqual, greater);
    }

    /// <summary>
    ///     A comparison for a type that spells out no ordering of its own, left for the database to
    ///     order as it orders the column.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the strongly-typed-id case, and it is not the dead end it looks like. A
    ///         <c>readonly record struct</c> over a string gets equality from the compiler and nothing
    ///         else, but the column behind it is <c>text</c> and the database knows perfectly well how
    ///         to order that. What is missing is only a way to say so in an expression tree.
    ///     </para>
    ///     <para>
    ///         So the node is built with the comparison's own <see cref="ExpressionType" /> and a
    ///         method that carries the meaning rather than an operator the type does not have. EF
    ///         translates a binary node by its kind and its operands, so what reaches SQL is the same
    ///         <c>col &gt; @p</c> a type with operators would have produced — with the value converter
    ///         applied to the parameter, as it is for every other comparison against that column.
    ///     </para>
    ///     <para>
    ///         The method is not a formality: an expression that is compiled rather than translated
    ///         has to mean something, and <see cref="Comparer{T}" /> is the closest thing .NET has to
    ///         the ordering being asked for. It throws for a type with no ordering at all, which is
    ///         the honest answer off the server.
    ///     </para>
    /// </remarks>
    private static BinaryExpression AsTheStoreOrdersIt(
        Expression left,
        Expression right,
        bool orEqual,
        bool greater)
    {
        var name = (greater, orEqual) switch
        {
            (true, false) => nameof(StoreOrder.After),
            (true, true) => nameof(StoreOrder.AtOrAfter),
            (false, false) => nameof(StoreOrder.Before),
            (false, true) => nameof(StoreOrder.AtOrBefore),
        };

        var method = MethodOf(name, left.Type);

        return (greater, orEqual) switch
        {
            (true, false) => Expression.GreaterThan(left, right, liftToNull: false, method),
            (true, true) => Expression.GreaterThanOrEqual(left, right, liftToNull: false, method),
            (false, false) => Expression.LessThan(left, right, liftToNull: false, method),
            (false, true) => Expression.LessThanOrEqual(left, right, liftToNull: false, method),
        };
    }

    private static MethodInfo MethodOf(string name, Type type)
        => typeof(StoreOrder)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type);

    /// <summary>
    ///     The type's own <c>CompareTo</c>, or <see langword="null" /> when it has none returning an
    ///     <see cref="int" />.
    /// </summary>
    private static MethodInfo? CompareToOf(Type type)
    {
        var compareTo = type.GetMethod(
            nameof(IComparable.CompareTo),
            BindingFlags.Public | BindingFlags.Instance,
            [type]);

        return compareTo?.ReturnType == typeof(int) ? compareTo : null;
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

    private static bool HasEqualityOperators(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        // Reference types always compare, by identity where nothing better is defined, and
        // Expression.Equal builds happily against them.
        return !underlying.IsValueType
            || underlying.IsPrimitive
            || underlying.IsEnum
            || underlying.GetMethod("op_Equality", BindingFlags.Public | BindingFlags.Static) is not null;
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

/// <summary>
///     What the comparison operators would have meant, for a type that does not define them. Only
///     ever called when an expression built by <see cref="Comparisons" /> is compiled rather than
///     translated to SQL.
/// </summary>
file static class StoreOrder
{
    internal static bool After<T>(T left, T right) => Comparer<T>.Default.Compare(left, right) > 0;

    internal static bool AtOrAfter<T>(T left, T right) => Comparer<T>.Default.Compare(left, right) >= 0;

    internal static bool Before<T>(T left, T right) => Comparer<T>.Default.Compare(left, right) < 0;

    internal static bool AtOrBefore<T>(T left, T right) => Comparer<T>.Default.Compare(left, right) <= 0;

    internal static bool Same<T>(T left, T right) => EqualityComparer<T>.Default.Equals(left, right);
}
