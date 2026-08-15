using System.Linq.Expressions;

namespace EFToolkit.Query.Filtering;

/// <summary>Declares which fields a free-text search covers.</summary>
/// <typeparam name="T">The element type being searched.</typeparam>
public class SearchSpecificationBuilder<T>
{
    private readonly List<Expression<Func<T, string?>>> _fields = [];
    private SearchMatch _match = SearchMatch.Contains;

    /// <summary>Adds a field to the search.</summary>
    /// <param name="selector">Selects the text to match against.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Fields are combined with <c>OR</c>, so a term matching any one of them matches the row.
    /// </remarks>
    public virtual SearchSpecificationBuilder<T> Field(Expression<Func<T, string?>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _fields.Add(selector);
        return this;
    }

    /// <summary>Sets how the term is matched. Defaults to <see cref="SearchMatch.Contains" />.</summary>
    /// <param name="match">The match mode.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual SearchSpecificationBuilder<T> Match(SearchMatch match)
    {
        _match = match;
        return this;
    }

    /// <summary>Produces the specification.</summary>
    /// <returns>The built specification.</returns>
    /// <exception cref="QueryNotSupportedException">The specification covers no fields.</exception>
    protected internal virtual SearchSpecification<T> Build()
    {
        if (_fields.Count == 0)
        {
            throw new QueryNotSupportedException(
                $"The search specification for {typeof(T).Name} covers no fields, so every term would "
                + "match nothing. Add at least one with Field(x => x.Name).");
        }

        return new SearchSpecification<T>(_fields, _match);
    }
}
