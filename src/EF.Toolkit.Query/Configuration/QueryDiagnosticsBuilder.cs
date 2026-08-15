namespace EFToolkit.Query.Configuration;

/// <summary>
///     Configures the advisory checks. Reached through
///     <see cref="QueryOptionsBuilder.Diagnostics(Action{QueryDiagnosticsBuilder})" />.
/// </summary>
/// <remarks>
///     Selecting checks is not enough on its own: the behavior starts at
///     <see cref="QueryWarningBehavior.Ignore" />, so a configuration that turns checks on without
///     calling <see cref="OnWarning" /> still reports nothing. That is the safer way round — enabling a
///     check by accident costs nothing, and the builder is a development-time tool that usually sits
///     behind an <c>if (environment.IsDevelopment())</c>.
/// </remarks>
public class QueryDiagnosticsBuilder
{
    /// <summary>Initializes a new instance of the <see cref="QueryDiagnosticsBuilder" /> class.</summary>
    /// <param name="options">The settings to start from.</param>
    public QueryDiagnosticsBuilder(QueryDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The settings configured so far.</summary>
    public QueryDiagnosticsOptions Options { get; protected set; }

    /// <summary>Reports when no declared index covers a paginated query's filter and ordering columns.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnMissingIndex() => Enable(QueryChecks.MissingIndex);

    /// <summary>Reports when a paginated query's ordering is not total.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnNonDeterministicOrder()
        => Enable(QueryChecks.NonDeterministicOrder);

    /// <summary>Reports when a page sits further than <see cref="QueryOptions.MaxOffsetRows" /> into the set.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnDeepOffset() => Enable(QueryChecks.DeepOffset);

    /// <summary>Reports when an <c>IN</c> list exceeds <see cref="QueryOptions.MaxInClauseValues" />.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnLargeInClause() => Enable(QueryChecks.LargeInClause);

    /// <summary>Reports when a page returns whole mapped entities rather than a projection.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnEntityProjection() => Enable(QueryChecks.EntityProjection);

    /// <summary>Reports when a page is taken over a collection <c>Include</c> in a single query.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnCollectionIncludeWithPaging()
        => Enable(QueryChecks.CollectionIncludeWithPaging);

    /// <summary>Runs every check.</summary>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder WarnOnEverything() => Enable(QueryChecks.All);

    /// <summary>Sets what happens when a check finds something.</summary>
    /// <param name="behavior">
    ///     <see cref="QueryWarningBehavior.Diagnostic" /> to publish and carry on, or
    ///     <see cref="QueryWarningBehavior.Throw" /> to fail the query.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public virtual QueryDiagnosticsBuilder OnWarning(QueryWarningBehavior behavior)
    {
        Options = Options with { Behavior = behavior };
        return this;
    }

    private QueryDiagnosticsBuilder Enable(QueryChecks check)
    {
        Options = Options with { Checks = Options.Checks | check };
        return this;
    }
}
