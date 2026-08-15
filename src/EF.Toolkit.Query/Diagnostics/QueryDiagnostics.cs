using System.Diagnostics;
using EFToolkit.Query.Configuration;

namespace EFToolkit.Query.Diagnostics;

/// <summary>The <see cref="DiagnosticListener" /> EF.Toolkit.Query publishes its advisories on.</summary>
/// <example>
///     <code>
///     DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
///     // ...
///     if (listener.Name == QueryDiagnostics.ListenerName)
///         listener.Subscribe(new AdvisoryObserver());
///     </code>
/// </example>
public static class QueryDiagnostics
{
    /// <summary>The listener's name.</summary>
    public const string ListenerName = "EF.Toolkit.Query";

    /// <summary>No declared index covers the query's filter and ordering columns.</summary>
    public const string MissingIndex = "EF.Toolkit.Query.MissingIndex";

    /// <summary>The ordering is not total, so page boundaries fall in arbitrary places.</summary>
    public const string NonDeterministicOrder = "EF.Toolkit.Query.NonDeterministicOrder";

    /// <summary>The page sits further into the set than the configured threshold.</summary>
    public const string DeepOffset = "EF.Toolkit.Query.DeepOffset";

    /// <summary>An <c>IN</c> list is longer than the configured threshold.</summary>
    public const string LargeInClause = "EF.Toolkit.Query.LargeInClause";

    /// <summary>The page returns whole mapped entities rather than a projection.</summary>
    public const string EntityProjection = "EF.Toolkit.Query.EntityProjection";

    /// <summary>The page is taken over a collection <c>Include</c> in a single query.</summary>
    public const string CollectionIncludeWithPaging = "EF.Toolkit.Query.CollectionIncludeWithPaging";

    internal static readonly DiagnosticListener Listener = new(ListenerName);

    internal static void Report(QueryAdvisory advisory)
    {
        var eventName = EventName(advisory.Check);

        if (Listener.IsEnabled(eventName))
        {
            Listener.Write(eventName, new QueryAdvisoryEvent(advisory));
        }
    }

    private static string EventName(QueryChecks check) => check switch
    {
        QueryChecks.MissingIndex => MissingIndex,
        QueryChecks.NonDeterministicOrder => NonDeterministicOrder,
        QueryChecks.DeepOffset => DeepOffset,
        QueryChecks.LargeInClause => LargeInClause,
        QueryChecks.EntityProjection => EntityProjection,
        QueryChecks.CollectionIncludeWithPaging => CollectionIncludeWithPaging,
        _ => ListenerName,
    };
}

/// <summary>Something the advisor found while inspecting a query.</summary>
/// <param name="Check">Which check found it.</param>
/// <param name="ElementType">The queried element type.</param>
/// <param name="Message">What was found, and what would resolve it.</param>
public sealed record QueryAdvisory(QueryChecks Check, string ElementType, string Message);

/// <summary>Published on <see cref="QueryDiagnostics.ListenerName" /> when an advisory is raised.</summary>
/// <param name="Advisory">What was found.</param>
public sealed record QueryAdvisoryEvent(QueryAdvisory Advisory);
