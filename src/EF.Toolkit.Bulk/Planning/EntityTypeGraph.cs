using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Bulk.Planning;

/// <summary>
///     Orders a model's entity types so that principals are written before their dependents.
/// </summary>
/// <remarks>
///     <para>
///         This is the ordering the transparent path gets free from EF's
///         <see cref="Microsoft.EntityFrameworkCore.Update.ICommandBatchPreparer" />. The explicit
///         API skips that pipeline — which is the whole reason it is fast — and so has to derive the
///         order itself.
///     </para>
///     <para>
///         It does so once per model rather than once per row: foreign keys are a property of the
///         schema, so a table-level sort answers the question for any number of entities. Row-level
///         layering is needed only where a table references itself, where the ordering genuinely
///         depends on the data.
///     </para>
/// </remarks>
internal static class EntityTypeGraph
{
    private static readonly ConcurrentDictionary<IModel, IReadOnlyList<IEntityType>> Cache = new();

    /// <summary>
    ///     Returns <paramref name="model" />'s entity types with every principal ahead of its
    ///     dependents.
    /// </summary>
    /// <exception cref="BulkNotSupportedException">
    ///     Thrown when two or more entity types reference each other, so no order satisfies every
    ///     foreign key.
    /// </exception>
    public static IReadOnlyList<IEntityType> TopologicalOrder(IModel model)
        => Cache.GetOrAdd(model, static m => Sort(m));

    /// <summary>
    ///     Whether <paramref name="entityType" /> has a foreign key pointing at itself, so its rows
    ///     have to be ordered among themselves.
    /// </summary>
    public static bool IsSelfReferencing(IEntityType entityType)
    {
        foreach (var foreignKey in entityType.GetForeignKeys())
        {
            if (foreignKey.PrincipalEntityType == entityType)
            {
                return true;
            }
        }

        return false;
    }

    private static List<IEntityType> Sort(IModel model)
    {
        var types = model.GetEntityTypes().Where(t => !t.IsOwned()).ToList();

        var dependents = new Dictionary<IEntityType, List<IEntityType>>();
        var inDegree = new Dictionary<IEntityType, int>();

        foreach (var type in types)
        {
            dependents[type] = [];
            inDegree[type] = 0;
        }

        foreach (var type in types)
        {
            foreach (var foreignKey in type.GetForeignKeys())
            {
                var principal = foreignKey.PrincipalEntityType;

                // A self-reference constrains rows, not tables, so it must not appear as a
                // table-level edge -- it would make every such type look like a cycle.
                if (principal == type || !inDegree.ContainsKey(principal))
                {
                    continue;
                }

                dependents[principal].Add(type);
                inDegree[type]++;
            }
        }

        // Kahn's algorithm, seeded in model order so the result is stable run to run.
        var ready = new Queue<IEntityType>(types.Where(t => inDegree[t] == 0));
        var ordered = new List<IEntityType>(types.Count);

        while (ready.Count > 0)
        {
            var type = ready.Dequeue();
            ordered.Add(type);

            foreach (var dependent in dependents[type])
            {
                if (--inDegree[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        if (ordered.Count != types.Count)
        {
            var cyclic = types.Where(t => inDegree[t] > 0).Select(t => t.DisplayName());

            throw new BulkNotSupportedException(
                "These entity types reference each other, so no insert order satisfies every "
                + $"foreign key: {string.Join(", ", cyclic)}. Breaking such a cycle needs a second "
                + "pass that fills the foreign keys after both rows exist, which SaveChanges() "
                + "does and IncludeGraph does not.");
        }

        return ordered;
    }
}
