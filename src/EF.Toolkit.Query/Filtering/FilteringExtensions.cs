using System.Linq.Expressions;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;
using EFToolkit.Query.Filtering;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Composes the filters a search or list endpoint applies conditionally.</summary>
/// <remarks>
///     These exist because the alternative — an <c>if</c> per optional filter, reassigning the query
///     each time — is where the same four-line block gets copied into every endpoint and one copy
///     eventually differs. Nothing here changes what EF does with the resulting query.
/// </remarks>
public static class FilteringExtensions
{
    /// <summary>Applies <paramref name="predicate" /> only when <paramref name="condition" /> holds.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="condition">Whether to apply the filter.</param>
    /// <param name="predicate">The filter.</param>
    /// <returns>
    ///     The filtered query, or <paramref name="source" /> itself when the condition does not hold —
    ///     the expression tree is left exactly as it was, so an unapplied filter cannot affect the SQL
    ///     or the compiled-query cache key.
    /// </returns>
    public static IQueryable<T> WhereIf<T>(
        this IQueryable<T> source,
        bool condition,
        Expression<Func<T, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return condition ? source.Where(predicate) : source;
    }

    /// <summary>Applies a filter built from <paramref name="value" /> when it is not null.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TValue">The optional value's type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="value">The optional value, typically straight off a request.</param>
    /// <param name="predicate">Builds the filter from the value, once it is known to be present.</param>
    /// <returns>The filtered query, or <paramref name="source" /> when the value is null.</returns>
    public static IQueryable<T> WhereIfNotNull<T, TValue>(
        this IQueryable<T> source,
        TValue? value,
        Func<TValue, Expression<Func<T, bool>>> predicate)
        where TValue : struct
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return value is { } present ? source.Where(predicate(present)) : source;
    }

    /// <summary>Applies a filter built from <paramref name="value" /> when it is not null.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TValue">The optional value's type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="value">The optional value, typically straight off a request.</param>
    /// <param name="predicate">Builds the filter from the value, once it is known to be present.</param>
    /// <returns>The filtered query, or <paramref name="source" /> when the value is null.</returns>
    public static IQueryable<T> WhereIfNotNull<T, TValue>(
        this IQueryable<T> source,
        TValue? value,
        Func<TValue, Expression<Func<T, bool>>> predicate)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return value is not null ? source.Where(predicate(value)) : source;
    }

    /// <summary>Keeps rows whose <paramref name="selector" /> is one of <paramref name="values" />.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TValue">The compared value's type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="selector">Selects the column to compare.</param>
    /// <param name="values">The values to accept.</param>
    /// <returns>The filtered query.</returns>
    /// <remarks>
    ///     An empty set matches nothing, which is the same answer SQL's <c>IN ()</c> gives and the
    ///     opposite of the "no filter" an empty list is sometimes assumed to mean. Use
    ///     <see cref="WhereIf" /> when the intent is to skip the filter entirely.
    /// </remarks>
    public static IQueryable<T> WhereIn<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        IEnumerable<TValue> values)
        => WhereInCore(source, selector, values, context: null);

    /// <summary>
    ///     Keeps rows whose <paramref name="selector" /> is one of <paramref name="values" />, raising
    ///     the large-IN-list advisory when configured to.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TValue">The compared value's type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="context">The context. Must be configured with <c>UseQueryHelpers()</c>.</param>
    /// <param name="selector">Selects the column to compare.</param>
    /// <param name="values">The values to accept.</param>
    /// <returns>The filtered query.</returns>
    public static IQueryable<T> WhereIn<T, TValue>(
        this IQueryable<T> source,
        DbContext context,
        Expression<Func<T, TValue>> selector,
        IEnumerable<TValue> values)
    {
        ArgumentNullException.ThrowIfNull(context);
        return WhereInCore(source, selector, values, context);
    }

    /// <summary>
    ///     Keeps rows whose <paramref name="selector" /> falls in the half-open interval
    ///     <c>[from, to)</c>. Either bound may be omitted.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <typeparam name="TValue">The compared value's type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="selector">Selects the column to compare.</param>
    /// <param name="from">The inclusive lower bound, or <see langword="null" /> for unbounded.</param>
    /// <param name="to">The exclusive upper bound, or <see langword="null" /> for unbounded.</param>
    /// <returns>The filtered query, or <paramref name="source" /> when both bounds are omitted.</returns>
    /// <remarks>
    ///     Half-open on purpose. A date range written as <c>&gt;= from &amp;&amp; &lt;= to</c> either
    ///     drops the last day's rows or includes one instant of the next, depending on whether the
    ///     column carries a time — and which of the two it does is the classic off-by-one in a
    ///     reporting filter. With <c>[from, to)</c>, consecutive ranges tile exactly and nothing is
    ///     counted twice.
    /// </remarks>
    public static IQueryable<T> WhereBetween<T, TValue>(
        this IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        TValue? from,
        TValue? to)
        where TValue : struct
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        if (from is null && to is null)
        {
            return source;
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        var column = ParameterRebinder.Rebind(selector.Body, selector.Parameters[0], parameter);
        Expression? predicate = null;

        if (from is { } lower)
        {
            predicate = Comparisons.GreaterThanOrEqual(
                column,
                CapturedValue.Of(lower, typeof(TValue)));
        }

        if (to is { } upper)
        {
            var below = Comparisons.LessThan(column, CapturedValue.Of(upper, typeof(TValue)));
            predicate = predicate is null ? below : Expression.AndAlso(predicate, below);
        }

        return source.Where(Expression.Lambda<Func<T, bool>>(predicate!, parameter));
    }

    /// <summary>Keeps rows matching a free-text term across the specification's fields.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The query to filter.</param>
    /// <param name="specification">The fields the search covers.</param>
    /// <param name="term">
    ///     What to search for. <see langword="null" />, empty or whitespace applies no filter.
    /// </param>
    /// <returns>The filtered query, or <paramref name="source" /> when the term is blank.</returns>
    public static IQueryable<T> Search<T>(
        this IQueryable<T> source,
        SearchSpecification<T> specification,
        string? term)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(specification);

        return specification.Build(term) is { } predicate ? source.Where(predicate) : source;
    }

    private static IQueryable<T> WhereInCore<T, TValue>(
        IQueryable<T> source,
        Expression<Func<T, TValue>> selector,
        IEnumerable<TValue> values,
        DbContext? context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(values);

        var materialised = values as IReadOnlyList<TValue> ?? values.ToArray();

        if (context is not null)
        {
            var options = QueryConfiguration.Required(context, nameof(WhereIn));
            QueryAdvisor.InspectInClause(
                materialised.Count,
                selector.Body.ToString(),
                typeof(T).Name,
                options);
        }

        if (materialised.Count == 0)
        {
            // Short-circuited rather than handed to the provider, so an empty set means the same thing
            // on every database instead of whatever that provider does with an empty IN list.
            return source.Where(Predicates.False<T>());
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        var column = ParameterRebinder.Rebind(selector.Body, selector.Parameters[0], parameter);

        var contains = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Contains),
            [typeof(TValue)],
            CapturedValue.Of(materialised, typeof(IEnumerable<TValue>)),
            column);

        return source.Where(Expression.Lambda<Func<T, bool>>(contains, parameter));
    }
}
