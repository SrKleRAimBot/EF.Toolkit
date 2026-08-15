using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Internal;

namespace EFToolkit.Query.Tracking;

// EF1001: RelationalQueryContextFactory is marked as an internal EF API. Deriving from it is
// deliberate and the EF dependency is pinned to a single major (see Directory.Packages.props) for
// exactly this reason — the same trade EF.Toolkit.Bulk already makes against the update pipeline.
// The alternative seams are all either provider-overridden or run on the wrong side of the compiled
// query cache; the class remark explains the comparison in full.
#pragma warning disable EF1001

/// <summary>
///     Applies the ambient <see cref="QueryTrackingScope" /> to the context immediately before each
///     query is compiled or looked up. Installed by <c>UseQueryHelpers()</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is the only EF seam where a scope can be honoured correctly, and the reasoning is
///         worth recording because the obvious alternatives are all wrong.
///     </para>
///     <para>
///         EF caches compiled queries, and the cache key includes the context's
///         <c>ChangeTracker.QueryTrackingBehavior</c>. <c>QueryCompiler</c> creates the query context
///         first, then generates that cache key, then compiles on a miss. Setting the behavior from
///         <see cref="Create" /> therefore lands before the key is computed, so a tracked and an
///         untracked execution of the same LINQ expression occupy different cache entries. An
///         <c>IQueryExpressionInterceptor</c> runs only on a cache miss, so a scope expressed there
///         would be baked into the first compilation and silently served to every later execution.
///     </para>
///     <para>
///         <c>IQueryContextFactory</c> is also the only one of the candidate services that neither
///         the SQL Server nor the Npgsql provider overrides — replacing
///         <c>ICompiledQueryCacheKeyGenerator</c> or <c>IQueryCompilationContextFactory</c> from a
///         provider-neutral package would quietly drop provider behaviour.
///     </para>
/// </remarks>
public class TrackingScopeQueryContextFactory : RelationalQueryContextFactory
{
    private readonly ICurrentDbContext _currentContext;

    // What the context's own preference was before this factory first overwrote it. EF registers
    // IQueryContextFactory as scoped, so this instance and the field live exactly as long as the
    // DbContext whose behavior it is meddling with.
    private QueryTrackingBehavior? _contextPreference;

    /// <summary>Initializes a new instance of the <see cref="TrackingScopeQueryContextFactory" /> class.</summary>
    /// <param name="dependencies">EF's query context dependencies.</param>
    /// <param name="relationalDependencies">EF's relational query context dependencies.</param>
    public TrackingScopeQueryContextFactory(
        QueryContextDependencies dependencies,
        RelationalQueryContextDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _currentContext = dependencies.CurrentContext;
    }

    /// <inheritdoc />
    public override QueryContext Create()
    {
        ApplyAmbientScope();
        return base.Create();
    }

    private void ApplyAmbientScope()
    {
        var changeTracker = _currentContext.Context.ChangeTracker;

        if (QueryTrackingScope.Current is { } scope)
        {
            // Remember what the application had chosen, but only the first time — inside nested
            // scopes the "previous" value is this factory's own writing, not the context's.
            _contextPreference ??= changeTracker.QueryTrackingBehavior;
            changeTracker.QueryTrackingBehavior = scope.Behavior;
        }
        else if (_contextPreference is { } preference)
        {
            // The last scope on this flow has gone. Put back what the context had rather than the
            // provider default, so an application that set ChangeTracker.QueryTrackingBehavior by
            // hand still has it after a scope closes.
            changeTracker.QueryTrackingBehavior = preference;
            _contextPreference = null;
        }
    }
}
