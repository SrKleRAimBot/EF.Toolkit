using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Query.Filtering;

/// <summary>Builds <see cref="SearchSpecification{T}" /> instances.</summary>
public static class SearchSpecification
{
    /// <summary>Declares which fields a free-text search over <typeparamref name="T" /> covers.</summary>
    /// <typeparam name="T">The element type being searched.</typeparam>
    /// <param name="configure">Declares the fields and the match mode.</param>
    /// <returns>The specification.</returns>
    /// <example>
    ///     <code>
    ///     static readonly SearchSpecification&lt;Customer&gt; Search = SearchSpecification.For&lt;Customer&gt;(s => s
    ///         .Field(c => c.Name)
    ///         .Field(c => c.Email)
    ///         .Match(SearchMatch.StartsWith));
    ///     </code>
    /// </example>
    public static SearchSpecification<T> For<T>(Action<SearchSpecificationBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SearchSpecificationBuilder<T>();
        configure(builder);
        return builder.Build();
    }
}

/// <summary>The fields a free-text search covers, and how the term is matched against them.</summary>
/// <typeparam name="T">The element type being searched.</typeparam>
/// <remarks>
///     <para>
///         An allowlist, like <see cref="Sorting.SortSpecification{T}" />: the caller supplies a term,
///         never a field name, so a search cannot be pointed at a column the query did not offer.
///     </para>
///     <para>
///         Case sensitivity follows the column's collation, not this library — the comparison happens
///         in the database. So does the treatment of <c>%</c> and <c>_</c> inside the term, which EF
///         does not escape when it builds the <c>LIKE</c> pattern from a parameter: a term containing
///         them behaves as a wildcard. Strip or escape them before searching if that matters.
///     </para>
/// </remarks>
public sealed class SearchSpecification<T>
{
    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private readonly IReadOnlyList<Expression<Func<T, string?>>> _fields;
    private readonly SearchMatch _match;

    internal SearchSpecification(IReadOnlyList<Expression<Func<T, string?>>> fields, SearchMatch match)
    {
        _fields = fields;
        _match = match;
    }

    /// <summary>The number of fields the search covers.</summary>
    public int FieldCount => _fields.Count;

    /// <summary>Builds the predicate matching <paramref name="term" />.</summary>
    /// <param name="term">
    ///     What to search for. <see langword="null" />, empty or whitespace produces
    ///     <see langword="null" />, meaning "no filter" rather than "match nothing".
    /// </param>
    /// <returns>The predicate, or <see langword="null" /> when the term is blank.</returns>
    /// <remarks>
    ///     A blank term is a no-op rather than a refusal, because it is what an empty search box
    ///     sends and the expected answer is the unfiltered list.
    /// </remarks>
    public Expression<Func<T, bool>>? Build(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(T), "e");
        var value = CapturedValue.Of(term, typeof(string));
        Expression? predicate = null;

        foreach (var field in _fields)
        {
            var text = ParameterRebinder.Rebind(field.Body, field.Parameters[0], parameter);

            Expression clause = _match switch
            {
                SearchMatch.Exact => Expression.Equal(text, value),
                SearchMatch.StartsWith => Expression.Call(text, StartsWithMethod, value),
                _ => Expression.Call(text, ContainsMethod, value),
            };

            // A null column would make Contains and StartsWith throw when the query runs client-side,
            // and EF's own null semantics already exclude it server-side — the guard makes the two
            // agree, so a search behaves the same whichever way it is evaluated.
            if (_match != SearchMatch.Exact)
            {
                clause = Expression.AndAlso(
                    Expression.NotEqual(text, Expression.Constant(null, typeof(string))),
                    clause);
            }

            predicate = predicate is null ? clause : Expression.OrElse(predicate, clause);
        }

        return Expression.Lambda<Func<T, bool>>(predicate!, parameter);
    }
}
