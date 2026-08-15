namespace EFToolkit.Query.Configuration;

/// <summary>
///     The advisory checks EF.Toolkit.Query can run over a query before it executes. Every check is
///     answered from the EF model and the query's own expression tree — none of them touch the
///     database.
/// </summary>
[Flags]
public enum QueryChecks
{
    /// <summary>No checks. The default, and the only setting that costs nothing.</summary>
    None = 0,

    /// <summary>
    ///     No declared index covers the query's filter and ordering columns, so the server has to
    ///     sort the whole matching set to answer a page.
    /// </summary>
    MissingIndex = 1 << 0,

    /// <summary>
    ///     The ordering is not total — no unique column terminates it — so two rows that compare
    ///     equal may land on either side of a page boundary and be returned twice or not at all.
    /// </summary>
    NonDeterministicOrder = 1 << 1,

    /// <summary>
    ///     The page's offset exceeds <see cref="QueryOptions.MaxOffsetRows" />. The server must walk
    ///     and discard every skipped row, so the cost of a page grows with how far into the set it
    ///     sits.
    /// </summary>
    DeepOffset = 1 << 2,

    /// <summary>
    ///     A <c>WHERE ... IN</c> list exceeds <see cref="QueryOptions.MaxInClauseValues" />. SQL
    ///     Server caps a command at 2100 parameters, and the failure happens at execution rather
    ///     than at compile time.
    /// </summary>
    LargeInClause = 1 << 3,

    /// <summary>
    ///     The page returns whole mapped entities rather than a projection, so every column is read
    ///     and materialised whether or not the caller uses it.
    /// </summary>
    EntityProjection = 1 << 4,

    /// <summary>
    ///     The query pages over a collection <c>Include</c> in a single query. The join multiplies
    ///     the root rows, so <c>Skip</c>/<c>Take</c> count joined rows rather than roots and the page
    ///     comes back the wrong size.
    /// </summary>
    CollectionIncludeWithPaging = 1 << 5,

    /// <summary>Every check.</summary>
    All = MissingIndex
        | NonDeterministicOrder
        | DeepOffset
        | LargeInClause
        | EntityProjection
        | CollectionIncludeWithPaging,
}
