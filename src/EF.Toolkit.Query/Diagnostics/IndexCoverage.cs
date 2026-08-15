using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Query.Diagnostics;

/// <summary>Answers "could an index serve this query" from the EF model alone.</summary>
/// <remarks>
///     Model-only, so this sees indexes the model declares and nothing else. An index created by hand
///     — outside migrations, or by a DBA after the fact — is invisible here, which is why every
///     finding is worded as a possibility and why the whole feature is off by default.
/// </remarks>
internal static class IndexCoverage
{
    /// <summary>
    ///     Whether some declared index or key leads with the equality-filtered columns and then
    ///     continues with the ordering columns, in order.
    /// </summary>
    /// <remarks>
    ///     That prefix shape is the one the server can walk without sorting: equality columns first in
    ///     any order, because each is pinned to a single value, then the ordering columns in exactly
    ///     the order the query asks for.
    /// </remarks>
    internal static bool IsCovered(
        IEntityType entityType,
        IReadOnlyList<string> equalityPaths,
        IReadOnlyList<string> orderingPaths)
    {
        if (equalityPaths.Count == 0 && orderingPaths.Count == 0)
        {
            return true;
        }

        foreach (var candidate in Candidates(entityType))
        {
            if (Covers(candidate, equalityPaths, orderingPaths))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether <paramref name="orderingPaths" /> covers every column of some key or unique index,
    ///     which is what makes an ordering total.
    /// </summary>
    internal static bool IsTotalOrdering(IEntityType entityType, IReadOnlyList<string> orderingPaths)
    {
        if (orderingPaths.Count == 0)
        {
            return false;
        }

        var ordered = new HashSet<string>(orderingPaths, StringComparer.Ordinal);

        foreach (var key in entityType.GetKeys())
        {
            if (key.Properties.All(p => ordered.Contains(p.Name)))
            {
                return true;
            }
        }

        foreach (var index in entityType.GetIndexes())
        {
            if (index.IsUnique && index.Properties.All(p => ordered.Contains(p.Name)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Describes the declared indexes, for an advisory that has to name what is missing.</summary>
    internal static string Describe(IEntityType entityType)
    {
        var described = Candidates(entityType)
            .Select(static c => $"({string.Join(", ", c)})")
            .ToArray();

        return described.Length == 0 ? "none" : string.Join(", ", described);
    }

    private static bool Covers(
        IReadOnlyList<string> indexProperties,
        IReadOnlyList<string> equalityPaths,
        IReadOnlyList<string> orderingPaths)
    {
        var position = 0;
        var outstanding = new HashSet<string>(equalityPaths, StringComparer.Ordinal);

        while (position < indexProperties.Count && outstanding.Remove(indexProperties[position]))
        {
            position++;
        }

        if (outstanding.Count > 0)
        {
            return false;
        }

        foreach (var ordering in orderingPaths)
        {
            // An ordering column already pinned by an equality filter contributes nothing to the sort
            // — every row has the same value — so the index need not repeat it.
            if (equalityPaths.Contains(ordering, StringComparer.Ordinal))
            {
                continue;
            }

            if (position >= indexProperties.Count || indexProperties[position] != ordering)
            {
                return false;
            }

            position++;
        }

        return true;
    }

    private static List<IReadOnlyList<string>> Candidates(IEntityType entityType)
    {
        var candidates = new List<IReadOnlyList<string>>();

        foreach (var key in entityType.GetKeys())
        {
            candidates.Add(key.Properties.Select(static p => p.Name).ToArray());
        }

        foreach (var index in entityType.GetIndexes())
        {
            candidates.Add(index.Properties.Select(static p => p.Name).ToArray());
        }

        return candidates;
    }
}
