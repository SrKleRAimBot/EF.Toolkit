using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tracking;

/// <summary>
///     An ambient tracking preference. While one of these is alive, queries executed on any context
///     configured with <c>UseQueryHelpers()</c> on the same asynchronous flow use its
///     <see cref="Behavior" /> instead of the context's own.
/// </summary>
/// <remarks>
///     <para>
///         Created through <see cref="QueryTracking" /> rather than directly, and consumed by
///         <see cref="TrackingScopeQueryContextFactory" />.
///     </para>
///     <para>
///         The ambient value is pushed and popped rather than set and cleared, so nesting works: an
///         inner scope wins while it is alive, and disposing it restores the outer one rather than
///         leaving no scope at all.
///     </para>
///     <para>
///         Disposal walks past scopes already disposed rather than blindly restoring the value it
///         displaced. Without that, disposing out of order — an outer scope released while an inner
///         one is still alive — would either drop the live inner scope on the outer's disposal, or
///         resurrect the dead outer one on the inner's. Both are silent: the queries that follow just
///         track differently than the surrounding <c>using</c> says they should.
///     </para>
/// </remarks>
public sealed class QueryTrackingScope : IDisposable
{
    private static readonly AsyncLocal<QueryTrackingScope?> Ambient = new();

    private readonly QueryTrackingScope? _enclosing;
    private bool _disposed;

    internal QueryTrackingScope(QueryTrackingBehavior behavior)
    {
        Behavior = behavior;
        _enclosing = Ambient.Value;
        Ambient.Value = this;
    }

    /// <summary>
    ///     The innermost scope alive on this asynchronous flow, or <see langword="null" /> when there
    ///     is none.
    /// </summary>
    public static QueryTrackingScope? Current => Ambient.Value;

    /// <summary>The tracking behavior this scope imposes.</summary>
    public QueryTrackingBehavior Behavior { get; }

    /// <summary>
    ///     Restores the nearest enclosing scope still alive. Disposing more than once does nothing
    ///     after the first call.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var live = Ambient.Value;
        while (live is { _disposed: true })
        {
            live = live._enclosing;
        }

        Ambient.Value = live;
    }
}
