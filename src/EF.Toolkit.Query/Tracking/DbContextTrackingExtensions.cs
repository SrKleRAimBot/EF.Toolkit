using EFToolkit.Query.Tracking;

// Deliberately in EF's own namespace so these are visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Scopes a single context's tracking preference.</summary>
public static class DbContextTrackingExtensions
{
    /// <summary>
    ///     Sets this context's tracking preference for the life of the returned scope, then restores
    ///     whatever it was before.
    /// </summary>
    /// <param name="context">The context whose preference to change.</param>
    /// <param name="behavior">The behavior queries should use inside the scope.</param>
    /// <returns>The scope. Dispose it to restore the previous preference.</returns>
    /// <remarks>
    ///     The non-ambient counterpart to <see cref="QueryTracking.Begin" />: it changes one context
    ///     rather than one asynchronous flow, needs no <c>UseQueryHelpers()</c> and replaces no EF
    ///     service. Prefer it when the code already has the context in hand and there is no risk of
    ///     the scope escaping onto another thread. Scopes nest, and disposing restores the value from
    ///     before the scope opened rather than the provider default.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     using (context.BeginTrackingScope(QueryTrackingBehavior.NoTracking))
    ///     {
    ///         var rows = await context.Orders.ToListAsync();   // not tracked
    ///     }
    ///     </code>
    /// </example>
    public static IDisposable BeginTrackingScope(this DbContext context, QueryTrackingBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ContextTrackingScope(context, behavior);
    }

    private sealed class ContextTrackingScope : IDisposable
    {
        private readonly DbContext _context;
        private readonly QueryTrackingBehavior _previous;
        private bool _disposed;

        internal ContextTrackingScope(DbContext context, QueryTrackingBehavior behavior)
        {
            _context = context;
            _previous = context.ChangeTracker.QueryTrackingBehavior;
            context.ChangeTracker.QueryTrackingBehavior = behavior;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _context.ChangeTracker.QueryTrackingBehavior = _previous;
        }
    }
}
