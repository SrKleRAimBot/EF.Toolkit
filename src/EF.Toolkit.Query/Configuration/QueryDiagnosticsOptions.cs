namespace EFToolkit.Query.Configuration;

/// <summary>
///     Which advisory checks run, and what happens when one of them finds something. Immutable;
///     <see cref="QueryDiagnosticsBuilder" /> produces instances via <c>with</c>.
/// </summary>
public sealed record QueryDiagnosticsOptions
{
    /// <summary>Diagnostics off — the default, and what production should run.</summary>
    public static QueryDiagnosticsOptions Default { get; } = new();

    /// <summary>The checks to run. Defaults to <see cref="QueryChecks.None" />.</summary>
    public QueryChecks Checks { get; init; } = QueryChecks.None;

    /// <summary>What to do with a finding. Defaults to <see cref="QueryWarningBehavior.Ignore" />.</summary>
    public QueryWarningBehavior Behavior { get; init; } = QueryWarningBehavior.Ignore;

    /// <summary>
    ///     Whether any check will actually run. False whenever no check is selected or the behavior
    ///     is <see cref="QueryWarningBehavior.Ignore" />, which is the guard every entry point tests
    ///     before doing any advisory work at all.
    /// </summary>
    public bool IsEnabled => Checks != QueryChecks.None && Behavior != QueryWarningBehavior.Ignore;

    /// <summary>Whether <paramref name="check" /> is selected and reporting is switched on.</summary>
    /// <param name="check">The check to test.</param>
    /// <returns><see langword="true" /> when the check should run.</returns>
    public bool Runs(QueryChecks check) => IsEnabled && (Checks & check) == check;
}
