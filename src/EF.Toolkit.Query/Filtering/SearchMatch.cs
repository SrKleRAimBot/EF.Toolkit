namespace EFToolkit.Query.Filtering;

/// <summary>How a search term is matched against a field.</summary>
public enum SearchMatch
{
    /// <summary>
    ///     The field contains the term anywhere. Translates to <c>LIKE '%term%'</c>, which no index
    ///     can serve — the server reads every row.
    /// </summary>
    Contains = 0,

    /// <summary>
    ///     The field begins with the term. Translates to <c>LIKE 'term%'</c>, which an index on the
    ///     field <em>can</em> serve, so this is the one to prefer where the interface allows it.
    /// </summary>
    StartsWith = 1,

    /// <summary>The field equals the term.</summary>
    Exact = 2,
}
