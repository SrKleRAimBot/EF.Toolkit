namespace EFToolkit.Query.Configuration;

/// <summary>
///     Context-wide EF.Toolkit.Query settings, established by <c>UseQueryHelpers()</c>.
///     Immutable; <see cref="QueryOptionsBuilder" /> produces instances via <c>with</c>.
/// </summary>
/// <remarks>
///     These are defaults, not limits on what a caller may ask for at the query level — the point of
///     configuring them once is that every endpoint that does not care gets the same answer, not
///     that the ones that do care are prevented from differing. The exception is
///     <see cref="MaxPageSize" />, which is a ceiling by design.
/// </remarks>
public sealed record QueryOptions
{
    /// <summary>The default value of <see cref="DefaultPageSize" />.</summary>
    public const int DefaultDefaultPageSize = 20;

    /// <summary>The default value of <see cref="MaxPageSize" />.</summary>
    public const int DefaultMaxPageSize = 100;

    /// <summary>The default value of <see cref="MaxOffsetRows" />.</summary>
    public const int DefaultMaxOffsetRows = 50_000;

    /// <summary>The default value of <see cref="BatchSize" />.</summary>
    public const int DefaultBatchSize = 1_000;

    /// <summary>The default value of <see cref="MaxInClauseValues" />.</summary>
    public const int DefaultMaxInClauseValues = 2_000;

    /// <summary>Settings used when <c>UseQueryHelpers()</c> is called with no configuration.</summary>
    public static QueryOptions Default { get; } = new();

    /// <summary>
    ///     Page size applied when a <see cref="Paging.PageRequest" /> does not carry one. Defaults to
    ///     <see cref="DefaultDefaultPageSize" />.
    /// </summary>
    public int DefaultPageSize { get; init; } = DefaultDefaultPageSize;

    /// <summary>
    ///     Largest page size a caller may ask for. Larger requests are clamped to this rather than
    ///     refused. Defaults to <see cref="DefaultMaxPageSize" />.
    /// </summary>
    /// <remarks>
    ///     Clamping rather than throwing is deliberate. The value usually arrives from a query
    ///     string, so it is attacker-controlled; a ceiling that returns a smaller page keeps a
    ///     <c>?pageSize=1000000</c> from becoming a way to read the whole table in one request,
    ///     while a ceiling that throws just turns it into a way to generate 500s.
    /// </remarks>
    public int MaxPageSize { get; init; } = DefaultMaxPageSize;

    /// <summary>
    ///     Whether the first page is numbered 0 or 1. Defaults to
    ///     <see cref="PageNumbering.OneBased" />.
    /// </summary>
    public PageNumbering Numbering { get; init; } = PageNumbering.OneBased;

    /// <summary>
    ///     How an offset-paginated query establishes what lies beyond the page. Defaults to
    ///     <see cref="PageCountStrategy.TotalCount" />.
    /// </summary>
    public PageCountStrategy CountStrategy { get; init; } = PageCountStrategy.TotalCount;

    /// <summary>
    ///     Offset past which <see cref="QueryChecks.DeepOffset" /> reports. Defaults to
    ///     <see cref="DefaultMaxOffsetRows" />. Purely advisory — it never clamps or refuses.
    /// </summary>
    public int MaxOffsetRows { get; init; } = DefaultMaxOffsetRows;

    /// <summary>
    ///     Rows per batch for the streaming API when a call does not name one. Defaults to
    ///     <see cref="DefaultBatchSize" />.
    /// </summary>
    public int BatchSize { get; init; } = DefaultBatchSize;

    /// <summary>
    ///     Value count past which <see cref="QueryChecks.LargeInClause" /> reports. Defaults to
    ///     <see cref="DefaultMaxInClauseValues" />. Purely advisory.
    /// </summary>
    public int MaxInClauseValues { get; init; } = DefaultMaxInClauseValues;

    /// <summary>
    ///     Whether ambient <see cref="Tracking.QueryTracking" /> scopes apply to this context.
    ///     Defaults to <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     Honouring an ambient scope requires replacing EF's <c>IQueryContextFactory</c>. Turn this
    ///     off if something else in the application already replaces that service, in which case the
    ///     last <c>ReplaceService</c> call wins and one of the two silently stops working.
    /// </remarks>
    public bool TrackingScopes { get; init; } = true;

    /// <summary>Which advisory checks run. Off by default.</summary>
    public QueryDiagnosticsOptions Diagnostics { get; init; } = QueryDiagnosticsOptions.Default;

    /// <summary>The page number of the first page under <see cref="Numbering" />.</summary>
    public int FirstPageNumber => Numbering == PageNumbering.OneBased ? 1 : 0;
}
