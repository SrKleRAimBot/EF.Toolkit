using System.Linq.Expressions;
using EFToolkit.Query.Filtering;
using EFToolkit.Query.Sorting;

namespace EFToolkit.Query.Paging;

/// <summary>Builds the "rows strictly after this one" predicate for a keyset page.</summary>
/// <remarks>
///     <para>
///         For components <c>a, b, c</c> the predicate is the lexicographic expansion:
///     </para>
///     <code>
///     (a &gt; a0)
///     || (a == a0 &amp;&amp; b &gt; b0)
///     || (a == a0 &amp;&amp; b == b0 &amp;&amp; c &gt; c0)
///     </code>
///     <para>
///         The obvious shorthand — <c>a &gt; a0 &amp;&amp; b &gt; b0</c> — is wrong, and wrong in a way
///         that only shows up when the leading key has duplicates: it drops every row that shares
///         <c>a0</c> but sorts before <c>b0</c>, and those rows are never returned on any page.
///     </para>
///     <para>
///         SQL's row-value form, <c>(a, b) &gt; (a0, b0)</c>, says the same thing in one comparison and
///         PostgreSQL supports it, but SQL Server does not and EF does not translate tuple comparison
///         on either. The expansion translates everywhere.
///     </para>
/// </remarks>
internal static class KeysetPredicate
{
    /// <summary>
    ///     Builds the predicate selecting rows after <paramref name="values" /> under the ordering
    ///     described by <paramref name="components" />.
    /// </summary>
    /// <param name="components">The ordering, in priority order.</param>
    /// <param name="values">One boundary value per component, already converted to its key type.</param>
    /// <param name="backward">
    ///     Whether the page is being read backwards, which flips every comparison.
    /// </param>
    internal static Expression<Func<T, bool>> After<T>(
        IReadOnlyList<SortTerm> components,
        IReadOnlyList<object?> values,
        bool backward)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        Expression? predicate = null;

        for (var i = 0; i < components.Count; i++)
        {
            // Rows tying on every earlier component...
            Expression? clause = null;

            for (var j = 0; j < i; j++)
            {
                var equality = Comparisons.Equal(
                    Key(components[j], parameter),
                    Boundary(components[j], values[j]));

                clause = clause is null ? equality : Expression.AndAlso(clause, equality);
            }

            // ...and strictly past the boundary on this one.
            var ascending = components[i].Direction == SortDirection.Ascending;
            var key = Key(components[i], parameter);
            var boundary = Boundary(components[i], values[i]);

            var strict = ascending != backward
                ? Comparisons.GreaterThan(key, boundary)
                : Comparisons.LessThan(key, boundary);

            clause = clause is null ? strict : Expression.AndAlso(clause, strict);
            predicate = predicate is null ? clause : Expression.OrElse(predicate, clause);
        }

        return predicate is null
            ? Predicates.True<T>()
            : Expression.Lambda<Func<T, bool>>(predicate, parameter);
    }

    private static Expression Key(SortTerm component, ParameterExpression parameter)
        => ParameterRebinder.Rebind(
            component.KeySelector.Body,
            component.KeySelector.Parameters[0],
            parameter);

    private static MemberExpression Boundary(SortTerm component, object? value)
        => CapturedValue.Of(value, component.KeyType);
}
