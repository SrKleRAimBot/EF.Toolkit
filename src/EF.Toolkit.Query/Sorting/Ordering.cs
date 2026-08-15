using System.Reflection;

namespace EFToolkit.Query.Sorting;

/// <summary>Applies resolved <see cref="SortTerm" />s to a queryable.</summary>
/// <remarks>
///     The terms carry their key selectors as <c>LambdaExpression</c>, because a specification holds
///     terms of many different key types in one list. Closing <c>Queryable.OrderBy</c> over each key
///     type by reflection is what turns them back into ordinary EF ordering calls, so what reaches
///     the provider is indistinguishable from hand-written <c>OrderBy</c>/<c>ThenBy</c>.
/// </remarks>
internal static class Ordering
{
    private static readonly MethodInfo OrderByMethod = Find(nameof(Queryable.OrderBy));
    private static readonly MethodInfo OrderByDescendingMethod = Find(nameof(Queryable.OrderByDescending));
    private static readonly MethodInfo ThenByMethod = Find(nameof(Queryable.ThenBy));
    private static readonly MethodInfo ThenByDescendingMethod = Find(nameof(Queryable.ThenByDescending));

    internal static IOrderedQueryable<T> Apply<T>(IQueryable<T> source, IReadOnlyList<SortTerm> terms)
    {
        IQueryable<T> ordered = source;

        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            var ascending = term.Direction == SortDirection.Ascending;

            var method = i == 0
                ? ascending ? OrderByMethod : OrderByDescendingMethod
                : ascending ? ThenByMethod : ThenByDescendingMethod;

            ordered = (IQueryable<T>)method
                .MakeGenericMethod(typeof(T), term.KeyType)
                .Invoke(null, [ordered, term.KeySelector])!;
        }

        return (IOrderedQueryable<T>)ordered;
    }

    private static MethodInfo Find(string name)
        => typeof(Queryable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == name
                && m.GetGenericArguments().Length == 2
                && m.GetParameters().Length == 2);
}
