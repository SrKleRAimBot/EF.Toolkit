using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tracking;

/// <summary>
///     Opens ambient tracking scopes. A scope applies to every query executed on the same
///     asynchronous flow, on any context configured with <c>UseQueryHelpers()</c>, until it is
///     disposed.
/// </summary>
/// <example>
///     <code>
///     using (QueryTracking.NoTracking())
///     {
///         await context.Orders.ToListAsync();          // not tracked
///
///         using (QueryTracking.Tracking())
///         {
///             await context.Orders.ToListAsync();      // tracked — the innermost scope wins
///         }
///
///         await context.Orders.ToListAsync();          // not tracked again
///     }
///     </code>
/// </example>
/// <remarks>
///     <para>
///         An explicit <c>AsNoTracking()</c> or <c>AsTracking()</c> written on the query itself beats
///         the scope, because EF applies the operator it finds in the expression tree over the
///         context-level preference the scope sets.
///     </para>
///     <para>
///         The scope follows the asynchronous flow, so work started with <c>Task.Run</c> inside the
///         <c>using</c> inherits it, but work that outlives the <c>using</c> does not. It has no
///         effect on <c>SaveChanges</c>, which is not a query.
///     </para>
/// </remarks>
public static class QueryTracking
{
    /// <summary>The behavior imposed by the innermost live scope, or <see langword="null" /> when there is none.</summary>
    public static QueryTrackingBehavior? Current => QueryTrackingScope.Current?.Behavior;

    /// <summary>Opens a scope in which queries do not track their results.</summary>
    /// <returns>The scope. Dispose it to restore the enclosing preference.</returns>
    public static QueryTrackingScope NoTracking()
        => Begin(QueryTrackingBehavior.NoTracking);

    /// <summary>
    ///     Opens a scope in which queries do not track their results but still resolve identity, so
    ///     one row read twice in a query produces one instance.
    /// </summary>
    /// <returns>The scope. Dispose it to restore the enclosing preference.</returns>
    public static QueryTrackingScope NoTrackingWithIdentityResolution()
        => Begin(QueryTrackingBehavior.NoTrackingWithIdentityResolution);

    /// <summary>
    ///     Opens a scope in which queries track their results. Useful for carving a tracked section
    ///     out of a wider no-tracking scope.
    /// </summary>
    /// <returns>The scope. Dispose it to restore the enclosing preference.</returns>
    public static QueryTrackingScope Tracking()
        => Begin(QueryTrackingBehavior.TrackAll);

    /// <summary>Opens a scope imposing <paramref name="behavior" />.</summary>
    /// <param name="behavior">The tracking behavior queries inside the scope should use.</param>
    /// <returns>The scope. Dispose it to restore the enclosing preference.</returns>
    public static QueryTrackingScope Begin(QueryTrackingBehavior behavior)
        => new(behavior);
}
