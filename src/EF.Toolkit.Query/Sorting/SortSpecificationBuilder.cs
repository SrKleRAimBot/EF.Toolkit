using System.Linq.Expressions;

namespace EFToolkit.Query.Sorting;

/// <summary>Declares which fields a caller may sort by, and how the ordering is made total.</summary>
/// <typeparam name="T">The element type being ordered.</typeparam>
public class SortSpecificationBuilder<T>
{
    private readonly Dictionary<string, SortTerm> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SortField> _defaults = [];
    private SortTerm? _tiebreaker;

    /// <summary>Allows callers to sort by <paramref name="name" />.</summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="name">
    ///     The external field name, matched case-insensitively. This is the only string a caller can
    ///     supply, and it is looked up in this allowlist rather than resolved against the model, so an
    ///     API-supplied sort field cannot reach a column that was not offered.
    /// </param>
    /// <param name="keySelector">Selects the key to order by.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual SortSpecificationBuilder<T> Allow<TKey>(string name, Expression<Func<T, TKey>> keySelector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(keySelector);

        if (!_allowed.TryAdd(name, SortTerm.Create(name, keySelector, SortDirection.Ascending)))
        {
            throw new QueryNotSupportedException(
                $"The sort field '{name}' is declared more than once on this specification. Field "
                + "names are matched case-insensitively, so two declarations differing only in case "
                + "collide. Remove one, or give them distinct names.");
        }

        return this;
    }

    /// <summary>Sets the ordering used when a request names none.</summary>
    /// <param name="name">An allowed field name.</param>
    /// <param name="direction">Which way to order by it.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     Call more than once to build a multi-term default, in the order the calls are made.
    /// </remarks>
    public virtual SortSpecificationBuilder<T> DefaultOrder(
        string name,
        SortDirection direction = SortDirection.Ascending)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _defaults.Add(new SortField(name, direction));
        return this;
    }

    /// <summary>
    ///     Sets the term appended to every ordering, so that no two rows ever compare equal.
    /// </summary>
    /// <typeparam name="TKey">The key's type.</typeparam>
    /// <param name="keySelector">
    ///     Selects a unique key — in practice the primary key. It is not offered to callers as a sort
    ///     field unless it is also declared with <see cref="Allow" />.
    /// </param>
    /// <param name="direction">Which way to order by it.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     This is what makes paginated results correct. Without a unique final term the ordering is
    ///     partial: rows that tie may come back in a different order on each execution, so a row can
    ///     appear on two consecutive pages or on neither, and nothing in the result hints that it
    ///     happened.
    /// </remarks>
    public virtual SortSpecificationBuilder<T> Tiebreaker<TKey>(
        Expression<Func<T, TKey>> keySelector,
        SortDirection direction = SortDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _tiebreaker = SortTerm.Create("(tiebreaker)", keySelector, direction);
        return this;
    }

    /// <summary>Produces the specification.</summary>
    /// <returns>The built specification.</returns>
    /// <exception cref="QueryNotSupportedException">
    ///     The specification allows nothing, or would leave a request that names no ordering with no
    ///     ordering at all.
    /// </exception>
    protected internal virtual SortSpecification<T> Build()
    {
        if (_allowed.Count == 0)
        {
            throw new QueryNotSupportedException(
                $"The sort specification for {typeof(T).Name} allows no fields, so every request "
                + "would be refused. Declare at least one with Allow(\"name\", x => x.Property).");
        }

        if (_defaults.Count == 0 && _tiebreaker is null)
        {
            throw new QueryNotSupportedException(
                $"The sort specification for {typeof(T).Name} has neither a default ordering nor a "
                + "tiebreaker, so a request that names no fields would produce an unordered query and "
                + "paginating it would return rows in whatever order the server happened to use. Call "
                + "DefaultOrder(\"name\") to choose an ordering, Tiebreaker(x => x.Id) to guarantee a "
                + "total one, or both.");
        }

        foreach (var field in _defaults)
        {
            if (!_allowed.ContainsKey(field.Name))
            {
                throw new QueryNotSupportedException(
                    $"The default ordering names '{field.Name}', which is not an allowed sort field. "
                    + $"Allowed fields are: {string.Join(", ", _allowed.Keys.Order(StringComparer.Ordinal))}.");
            }
        }

        return new SortSpecification<T>(_allowed, _defaults, _tiebreaker);
    }
}
