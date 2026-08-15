namespace EFToolkit.Query.Configuration;

/// <summary>
///     Configures EF.Toolkit.Query for a context. Reached through <c>UseQueryHelpers()</c>.
/// </summary>
/// <example>
///     <code>
///     options.UseNpgsql(connectionString)
///            .UseQueryHelpers(q => q
///                .DefaultPageSize(25)
///                .MaxPageSize(200)
///                .CountStrategy(PageCountStrategy.HasNextProbe));
///     </code>
/// </example>
public class QueryOptionsBuilder
{
    /// <summary>Initializes a new instance of the <see cref="QueryOptionsBuilder" /> class.</summary>
    /// <param name="options">The settings to start from.</param>
    public QueryOptionsBuilder(QueryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The settings configured so far.</summary>
    public QueryOptions Options { get; protected set; }

    /// <summary>Sets the page size used when a request does not carry one.</summary>
    /// <param name="pageSize">Rows per page. Must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder DefaultPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        Options = Options with { DefaultPageSize = pageSize };
        return this;
    }

    /// <summary>Sets the largest page size a caller may ask for. Larger requests are clamped.</summary>
    /// <param name="pageSize">The ceiling. Must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder MaxPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        Options = Options with { MaxPageSize = pageSize };
        return this;
    }

    /// <summary>Sets whether the first page is numbered 0 or 1.</summary>
    /// <param name="numbering">The numbering base.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder PageNumbering(PageNumbering numbering)
    {
        Options = Options with { Numbering = numbering };
        return this;
    }

    /// <summary>Sets how an offset-paginated query establishes what lies beyond the page.</summary>
    /// <param name="strategy">The strategy.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder CountStrategy(PageCountStrategy strategy)
    {
        Options = Options with { CountStrategy = strategy };
        return this;
    }

    /// <summary>Sets the offset past which the deep-offset advisory reports.</summary>
    /// <param name="rows">The offset threshold. Must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder MaxOffsetRows(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Options = Options with { MaxOffsetRows = rows };
        return this;
    }

    /// <summary>Sets the rows per batch used by the streaming API when a call does not name one.</summary>
    /// <param name="rows">Rows per batch. Must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder BatchSize(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        Options = Options with { BatchSize = rows };
        return this;
    }

    /// <summary>Sets the <c>IN</c>-list length past which the large-in-clause advisory reports.</summary>
    /// <param name="values">The value-count threshold. Must be at least 1.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder MaxInClauseValues(int values)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(values, 1);
        Options = Options with { MaxInClauseValues = values };
        return this;
    }

    /// <summary>
    ///     Turns ambient <see cref="Tracking.QueryTracking" /> scopes off for this context, leaving
    ///     EF's <c>IQueryContextFactory</c> alone.
    /// </summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder WithoutTrackingScopes()
    {
        Options = Options with { TrackingScopes = false };
        return this;
    }

    /// <summary>Configures the advisory checks. Everything under here is off by default.</summary>
    /// <param name="configure">Selects the checks and what to do with a finding.</param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryOptionsBuilder Diagnostics(Action<QueryDiagnosticsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new QueryDiagnosticsBuilder(Options.Diagnostics);
        configure(builder);
        Options = Options with { Diagnostics = builder.Options };
        return this;
    }
}
