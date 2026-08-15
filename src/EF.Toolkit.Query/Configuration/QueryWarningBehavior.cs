namespace EFToolkit.Query.Configuration;

/// <summary>What happens when the advisor finds something worth reporting.</summary>
public enum QueryWarningBehavior
{
    /// <summary>
    ///     Report nothing. The advisor's checks are not run at all, so an application that leaves
    ///     this alone pays nothing for the feature existing. The default.
    /// </summary>
    Ignore = 0,

    /// <summary>
    ///     Write the advisory to the <c>EF.Toolkit.Query</c> diagnostic listener and carry on. The
    ///     query still executes and returns its normal result.
    /// </summary>
    Diagnostic = 1,

    /// <summary>
    ///     Write the advisory and then throw <see cref="QueryNotSupportedException" />.
    /// </summary>
    /// <remarks>
    ///     Intended for test suites and local development, where a missing index should fail loudly
    ///     rather than be read past. Enabling this in production turns an advisory into an outage.
    /// </remarks>
    Throw = 2,
}
