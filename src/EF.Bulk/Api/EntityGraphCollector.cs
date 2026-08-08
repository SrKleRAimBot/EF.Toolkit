using EFBulk.Planning;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFBulk.Api;

/// <summary>
///     Walks an object graph from a set of roots, grouping everything it reaches by entity type.
/// </summary>
internal static class EntityGraphCollector
{
    /// <summary>
    ///     Collects every entity reachable from <paramref name="roots" />.
    /// </summary>
    /// <param name="model">The model the entities belong to.</param>
    /// <param name="rootType">The entity type of the roots.</param>
    /// <param name="roots">The entities to start from.</param>
    /// <returns>Entities by type, each list in discovery order.</returns>
    public static Dictionary<IEntityType, List<object>> Collect(
        IModel model,
        IEntityType rootType,
        IEnumerable<object> roots)
    {
        var byType = new Dictionary<IEntityType, List<object>>();

        // Reference equality, not Equals: two distinct instances with the same key are two rows to
        // write, and an entity that overrides Equals must not silently collapse them.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<(object Entity, IEntityType Type)>();

        foreach (var root in roots)
        {
            if (root is not null && seen.Add(root))
            {
                pending.Enqueue((root, rootType));
            }
        }

        while (pending.Count > 0)
        {
            var (entity, entityType) = pending.Dequeue();

            if (!byType.TryGetValue(entityType, out var list))
            {
                list = [];
                byType[entityType] = list;
            }

            list.Add(entity);

            foreach (var navigation in EntityGraphPlan.For(entityType).Navigations)
            {
                foreach (var related in navigation.Read(entity))
                {
                    if (seen.Add(related))
                    {
                        // Resolve the runtime type so a derived entity is written to its own table
                        // rather than the navigation's declared one.
                        pending.Enqueue((related, model.FindEntityType(related.GetType()) ?? navigation.Target));
                    }
                }
            }
        }

        return byType;
    }

    /// <summary>
    ///     Splits a self-referencing type's entities into layers, parents before children.
    /// </summary>
    /// <remarks>
    ///     Table-level ordering cannot resolve a table that references itself — the constraint is
    ///     between rows, so the answer depends on the data. Each layer references only earlier
    ///     layers, so it can be written as one bulk operation.
    /// </remarks>
    public static List<List<object>> LayerBySelfReference(
        IEntityType entityType,
        List<object> entities)
    {
        // Only foreign keys pointing back at this same type impose an order between these rows.
        var selfReferences = EntityGraphPlan.For(entityType).Fixups
            .Where(f => f.PrincipalType == entityType)
            .ToList();

        if (selfReferences.Count == 0)
        {
            return [entities];
        }

        var index = new HashSet<object>(entities, ReferenceEqualityComparer.Instance);
        var depths = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var layers = new List<List<object>>();

        foreach (var entity in entities)
        {
            var depth = DepthOf(entity, selfReferences, index, depths, entityType);

            while (layers.Count <= depth)
            {
                layers.Add([]);
            }

            layers[depth].Add(entity);
        }

        return layers;
    }

    private static int DepthOf(
        object entity,
        List<ForeignKeyFixup> selfReferences,
        HashSet<object> index,
        Dictionary<object, int> depths,
        IEntityType entityType)
    {
        if (depths.TryGetValue(entity, out var known))
        {
            return known;
        }

        // Seeded before recursing so a cycle in the data terminates instead of overflowing the
        // stack; a self-referencing loop is invalid anyway and the database will reject it.
        depths[entity] = 0;

        var depth = 0;
        foreach (var fixup in selfReferences)
        {
            var parent = fixup.GetPrincipal(entity);

            // A parent outside this batch already exists in the database, so it imposes no ordering.
            if (parent is not null && index.Contains(parent))
            {
                depth = Math.Max(
                    depth, DepthOf(parent, selfReferences, index, depths, entityType) + 1);
            }
        }

        depths[entity] = depth;
        return depth;
    }
}
