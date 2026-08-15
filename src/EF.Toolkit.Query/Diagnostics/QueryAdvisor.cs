using EFToolkit.Query.Configuration;
using EFToolkit.Query.Paging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Query.Diagnostics;

/// <summary>
///     Inspects a query before it executes and reports what looks likely to bite later. Everything it
///     knows comes from the EF model and the query's own expression tree; it never touches the
///     database.
/// </summary>
/// <remarks>
///     Every entry point returns immediately unless diagnostics are switched on, so an application
///     that leaves them alone — which is what production should do — pays one boolean per query.
/// </remarks>
internal static class QueryAdvisor
{
    internal static void InspectPage<T>(
        DbContext context,
        IQueryable<T> source,
        ResolvedPage page,
        QueryOptions options)
    {
        if (!options.Diagnostics.IsEnabled)
        {
            return;
        }

        var findings = new List<QueryAdvisory>();
        var entityType = context.Model.FindEntityType(typeof(T));
        var shape = QueryShapeProbe.Inspect(source.Expression);

        if (options.Diagnostics.Runs(QueryChecks.DeepOffset) && page.Offset > options.MaxOffsetRows)
        {
            findings.Add(new QueryAdvisory(
                QueryChecks.DeepOffset,
                typeof(T).Name,
                $"Page {page.PageNumber} starts at row {page.Offset}, past the configured "
                + $"MaxOffsetRows of {options.MaxOffsetRows}. The server has to walk and discard every "
                + "skipped row, so this page costs more than the last one did and the next costs more "
                + "again. Switch this query to ToKeysetPageAsync, which seeks straight to the "
                + "boundary."));
        }

        if (options.Diagnostics.Runs(QueryChecks.NonDeterministicOrder)
            && entityType is not null
            && !IndexCoverage.IsTotalOrdering(entityType, shape.OrderingPaths))
        {
            findings.Add(new QueryAdvisory(
                QueryChecks.NonDeterministicOrder,
                typeof(T).Name,
                shape.OrderingPaths.Count == 0
                    ? "The query is paginated but not ordered, so which rows land on which page is "
                        + "whatever the server found convenient — and it is free to answer differently "
                        + "next time. Order the query, ending in a unique column."
                    : $"The ordering ({string.Join(", ", shape.OrderingPaths)}) does not cover any key "
                        + "or unique index, so rows can tie. Tied rows may be returned on two "
                        + "consecutive pages or on neither. End the ordering with the primary key — a "
                        + "SortSpecification tiebreaker does this for every request."));
        }

        InspectCommon(findings, entityType, shape, options);
        Raise(findings, options);
    }

    internal static void InspectKeyset<T>(
        DbContext context,
        IQueryable<T> source,
        KeysetDefinition<T> keys,
        QueryOptions options)
    {
        if (!options.Diagnostics.IsEnabled)
        {
            return;
        }

        var findings = new List<QueryAdvisory>();
        var entityType = context.Model.FindEntityType(typeof(T));

        // The ordering comes from the definition rather than the tree — a keyset query is handed in
        // unordered — so the probe is only consulted for the filters and the includes.
        var probed = QueryShapeProbe.Inspect(source.Expression);
        var shape = probed with { OrderingPaths = keys.ColumnPaths };

        InspectCommon(findings, entityType, shape, options);
        Raise(findings, options);
    }

    internal static void InspectInClause(
        int valueCount,
        string path,
        string elementType,
        QueryOptions options)
    {
        if (!options.Diagnostics.Runs(QueryChecks.LargeInClause)
            || valueCount <= options.MaxInClauseValues)
        {
            return;
        }

        Raise(
            [
                new QueryAdvisory(
                    QueryChecks.LargeInClause,
                    elementType,
                    $"The IN list on '{path}' carries {valueCount} values, past the configured "
                    + $"MaxInClauseValues of {options.MaxInClauseValues}. SQL Server caps a command at "
                    + "2100 parameters and fails at execution rather than at compile time. Filter "
                    + "against a joined set, or split the values into batches."),
            ],
            options);
    }

    private static void InspectCommon(
        List<QueryAdvisory> findings,
        IEntityType? entityType,
        QueryShape shape,
        QueryOptions options)
    {
        if (options.Diagnostics.Runs(QueryChecks.MissingIndex)
            && entityType is not null
            && !IndexCoverage.IsCovered(entityType, shape.EqualityPaths, shape.OrderingPaths))
        {
            var wanted = shape.EqualityPaths.Concat(shape.OrderingPaths.Except(shape.EqualityPaths));

            findings.Add(new QueryAdvisory(
                QueryChecks.MissingIndex,
                entityType.ClrType.Name,
                $"No declared index on {entityType.DisplayName()} leads with "
                + $"({string.Join(", ", wanted)}), so the server has to sort every matching row to "
                + $"answer a page. Declared indexes are: {IndexCoverage.Describe(entityType)}. Add "
                + $"b.HasIndex({string.Join(", ", wanted.Select(p => $"x => x.{p}"))}) — or confirm the "
                + "index exists outside the EF model, which this check cannot see."));
        }

        if (options.Diagnostics.Runs(QueryChecks.EntityProjection) && entityType is not null)
        {
            findings.Add(new QueryAdvisory(
                QueryChecks.EntityProjection,
                entityType.ClrType.Name,
                $"The page returns {entityType.DisplayName()} itself, so every mapped column is read "
                + "and materialised whether or not the caller uses it. Project to the shape the caller "
                + "needs with Select before paging."));
        }

        if (options.Diagnostics.Runs(QueryChecks.CollectionIncludeWithPaging)
            && shape.HasInclude
            && !shape.IsSplitQuery
            && HasCollectionNavigation(entityType))
        {
            findings.Add(new QueryAdvisory(
                QueryChecks.CollectionIncludeWithPaging,
                entityType!.ClrType.Name,
                $"The page includes a collection navigation of {entityType.DisplayName()} in a single "
                + "query. The join multiplies each root row by its children, so Skip and Take count "
                + "joined rows rather than roots and the page comes back the wrong size. Call "
                + "AsSplitQuery(), or load the children separately."));
        }
    }

    /// <summary>
    ///     Whether the entity has any collection navigation at all. The probe cannot tell which
    ///     navigation an <c>Include</c> named without resolving the lambda against the model, and an
    ///     entity with no collections cannot have the problem either way.
    /// </summary>
    private static bool HasCollectionNavigation(IEntityType? entityType)
        => entityType is not null
            && (entityType.GetNavigations().Any(static n => n.IsCollection)
                || entityType.GetSkipNavigations().Any(static n => n.IsCollection));

    private static void Raise(List<QueryAdvisory> findings, QueryOptions options)
    {
        if (findings.Count == 0)
        {
            return;
        }

        foreach (var finding in findings)
        {
            QueryDiagnostics.Report(finding);
        }

        if (options.Diagnostics.Behavior != QueryWarningBehavior.Throw)
        {
            return;
        }

        // Every finding at once rather than the first: fixing one and rerunning to discover the next
        // turns a single review into several.
        throw new QueryNotSupportedException(
            "EF.Toolkit.Query diagnostics are configured to throw, and this query raised "
            + $"{findings.Count} advisory(s):{Environment.NewLine}{Environment.NewLine}"
            + string.Join(
                Environment.NewLine + Environment.NewLine,
                findings.Select(static f => $"[{f.Check}] {f.Message}")));
    }
}
