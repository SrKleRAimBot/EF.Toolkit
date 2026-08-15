namespace EFToolkit.Query.Sorting;

/// <summary>Builds <see cref="SortSpecification{T}" /> instances.</summary>
public static class SortSpecification
{
    /// <summary>Declares the orderings callers may ask for over <typeparamref name="T" />.</summary>
    /// <typeparam name="T">The element type being ordered.</typeparam>
    /// <param name="configure">Declares the allowed fields, the default ordering and the tiebreaker.</param>
    /// <returns>The specification.</returns>
    /// <example>
    ///     <code>
    ///     static readonly SortSpecification&lt;Order&gt; Sort = SortSpecification.For&lt;Order&gt;(s => s
    ///         .Allow("placed", o => o.PlacedAt)
    ///         .Allow("total", o => o.Total)
    ///         .DefaultOrder("placed", SortDirection.Descending)
    ///         .Tiebreaker(o => o.Id));
    ///     </code>
    /// </example>
    /// <remarks>
    ///     A specification is immutable and holds no per-query state, so declare it once as a static
    ///     field rather than rebuilding it per request.
    /// </remarks>
    public static SortSpecification<T> For<T>(Action<SortSpecificationBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new SortSpecificationBuilder<T>();
        configure(builder);
        return builder.Build();
    }
}

/// <summary>
///     The orderings a caller may ask for over <typeparamref name="T" />, and the guarantee that
///     whichever they pick, the result is totally ordered.
/// </summary>
/// <typeparam name="T">The element type being ordered.</typeparam>
public sealed class SortSpecification<T>
{
    private readonly Dictionary<string, SortTerm> _allowed;
    private readonly IReadOnlyList<SortField> _defaults;
    private readonly SortTerm? _tiebreaker;

    internal SortSpecification(
        Dictionary<string, SortTerm> allowed,
        IReadOnlyList<SortField> defaults,
        SortTerm? tiebreaker)
    {
        _allowed = allowed;
        _defaults = defaults;
        _tiebreaker = tiebreaker;
    }

    /// <summary>The field names a caller may sort by.</summary>
    public IReadOnlyCollection<string> AllowedFields => _allowed.Keys;

    /// <summary>The ordering applied when a request names none.</summary>
    public IReadOnlyList<SortField> DefaultOrder => _defaults;

    /// <summary>Whether a tiebreaker guarantees the ordering is total.</summary>
    public bool HasTiebreaker => _tiebreaker is not null;

    /// <summary>Orders <paramref name="source" /> by <paramref name="request" />.</summary>
    /// <param name="source">The query to order.</param>
    /// <param name="request">
    ///     What the caller asked for. <see langword="null" /> or <see cref="SortRequest.Empty" />
    ///     applies the default ordering.
    /// </param>
    /// <returns>The ordered query.</returns>
    /// <exception cref="QueryNotSupportedException">
    ///     The request names a field this specification does not allow, or names one twice.
    /// </exception>
    public IOrderedQueryable<T> Apply(IQueryable<T> source, SortRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Ordering.Apply(source, Resolve(request));
    }

    /// <summary>
    ///     Resolves <paramref name="request" /> against the allowlist, appending the tiebreaker.
    /// </summary>
    /// <remarks>
    ///     An unknown field throws rather than being skipped. Skipping it would return rows in an
    ///     order the caller did not ask for and cannot detect, and — because the field name usually
    ///     comes straight off a query string — would turn a client-side typo into a silently wrong
    ///     page.
    /// </remarks>
    internal IReadOnlyList<SortTerm> Resolve(SortRequest? request)
    {
        var requested = request is null || request.IsEmpty ? _defaults : request.Fields;
        var terms = new List<SortTerm>(requested.Count + 1);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in requested)
        {
            if (!_allowed.TryGetValue(field.Name, out var term))
            {
                throw new QueryNotSupportedException(
                    $"'{field.Name}' is not a sort field allowed for {typeof(T).Name}. Allowed fields "
                    + $"are: {string.Join(", ", _allowed.Keys.Order(StringComparer.Ordinal))}.");
            }

            if (!seen.Add(term.Name))
            {
                throw new QueryNotSupportedException(
                    $"The sort request names '{field.Name}' more than once. A field can only order the "
                    + "result one way, so the second occurrence would have no effect.");
            }

            terms.Add(term with { Direction = field.Direction });
        }

        AppendTiebreaker(terms);
        return terms;
    }

    /// <summary>
    ///     Appends the tiebreaker unless the ordering already ends in the same column, so that
    ///     ordering explicitly by the key does not order by it twice.
    /// </summary>
    private void AppendTiebreaker(List<SortTerm> terms)
    {
        if (_tiebreaker is null)
        {
            return;
        }

        foreach (var term in terms)
        {
            // Matched on the property path rather than the field name: the tiebreaker is not itself
            // an allowed field, so it has no name in common with the term that duplicates it.
            if (term.PropertyPath is not null && term.PropertyPath == _tiebreaker.PropertyPath)
            {
                return;
            }
        }

        terms.Add(_tiebreaker);
    }
}
