using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Testing;

/// <summary>One table a reset empties.</summary>
/// <param name="Schema">The schema, or <see langword="null" /> for the connection's default.</param>
/// <param name="Name">The table name.</param>
internal sealed record ResetTable(string? Schema, string Name)
{
    public override string ToString() => Schema is null ? Name : $"{Schema}.{Name}";
}

/// <summary>The tables a reset empties, in the order they can be emptied in.</summary>
/// <remarks>
///     Computed from the model rather than read from the database's catalog, which is what separates
///     this from Respawn: no round trip to plan, and only the tables this context owns are touched —
///     a database shared with another context, or carrying a migrations history table, keeps them.
/// </remarks>
internal sealed class ResetPlan
{
    private ResetPlan(
        IReadOnlyList<ResetTable> tables,
        IReadOnlySet<ResetTable> cyclicTables,
        bool hasExclusions)
    {
        Tables = tables;
        CyclicTables = cyclicTables;
        HasExclusions = hasExclusions;
    }

    /// <summary>Every table to empty, dependents before the principals they reference.</summary>
    public IReadOnlyList<ResetTable> Tables { get; }

    /// <summary>
    ///     The tables on a foreign-key cycle, self-references included. No order empties these one at a
    ///     time with constraints enforced, so the engines that delete row by row suspend them.
    /// </summary>
    public IReadOnlySet<ResetTable> CyclicTables { get; }

    /// <summary>Whether the caller excluded anything.</summary>
    public bool HasExclusions { get; }

    /// <summary>Plans the reset of every table <paramref name="model" /> maps.</summary>
    /// <param name="model">The context's runtime model.</param>
    /// <param name="options">Tables and entity types to leave alone.</param>
    public static ResetPlan Build(IModel model, ResetDataOptions options)
    {
        var relational = model.GetRelationalModel();
        var excluded = ExcludedTables(model, options);

        // ITable covers exactly what holds rows: views, SQL queries, functions and keyless types
        // with no table are separate kinds of object in the relational model, so they never get here.
        var included = relational.Tables
            .Where(t => !IsExcluded(t, excluded, options))
            .ToDictionary(t => new ResetTable(t.Schema, t.Name), t => t);

        RefuseRetainedDependents(relational, included);

        // Edges run dependent -> principal: the principal is the one that has to wait. A principal
        // outside the reset imposes no ordering — its rows stay, so references to it stay valid.
        var (ordered, cyclic) = Order(
            [.. included.Keys],
            n => included[n].ForeignKeyConstraints
                .Select(fk => new ResetTable(fk.PrincipalTable.Schema, fk.PrincipalTable.Name))
                .Where(included.ContainsKey));

        return new ResetPlan(ordered, cyclic, options.HasExclusions);
    }

    /// <summary>
    ///     Orders <paramref name="tables" /> dependents first, and finds the ones on a cycle.
    /// </summary>
    /// <param name="tables">Every table in the reset.</param>
    /// <param name="principals">The tables in the reset that a table references.</param>
    internal static (List<ResetTable> Ordered, HashSet<ResetTable> Cyclic) Order(
        IReadOnlyCollection<ResetTable> tables,
        Func<ResetTable, IEnumerable<ResetTable>> principals)
    {
        var nodes = tables
            .OrderBy(t => t.Schema, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

        var edges = nodes.ToDictionary(
            n => n,
            n => principals(n)
                .Distinct()
                .OrderBy(t => t.Schema, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList());

        var components = StronglyConnectedComponents(nodes, edges);

        var cyclic = new HashSet<ResetTable>();
        foreach (var component in components)
        {
            if (component.Count > 1 || edges[component[0]].Contains(component[0]))
            {
                cyclic.UnionWith(component);
            }
        }

        // Tarjan emits a component only after every component it reaches, so principals come out
        // first. Reversed, that is dependents first — the order a delete has to run in.
        var ordered = components
            .AsEnumerable()
            .Reverse()
            .SelectMany(c => c
                .OrderBy(t => t.Schema, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal))
            .ToList();

        return (ordered, cyclic);
    }

    /// <summary>Builds a plan directly, for tests of the SQL each engine is given.</summary>
    internal static ResetPlan Create(
        IReadOnlyList<ResetTable> tables,
        IEnumerable<ResetTable>? cyclicTables = null,
        bool hasExclusions = false)
        => new(tables, new HashSet<ResetTable>(cyclicTables ?? []), hasExclusions);

    // Every engine's reset suspends or defers foreign-key enforcement while it deletes, so a table
    // left out of the reset that references one being emptied would not stop the delete: on SQLite its
    // rows could be cascade-deleted at commit, and everywhere else they would be left pointing at rows
    // that no longer exist. Neither is what "exclude this table" asks for, so the plan refuses it.
    private static void RefuseRetainedDependents(
        IRelationalModel relational,
        Dictionary<ResetTable, ITable> included)
    {
        foreach (var table in relational.Tables)
        {
            var name = new ResetTable(table.Schema, table.Name);
            if (included.ContainsKey(name))
            {
                continue;
            }

            foreach (var foreignKey in table.ForeignKeyConstraints)
            {
                var principal = new ResetTable(foreignKey.PrincipalTable.Schema, foreignKey.PrincipalTable.Name);
                if (included.ContainsKey(principal))
                {
                    throw new InvalidOperationException(
                        $"Cannot reset '{principal}' while keeping '{name}': the kept table references it "
                        + $"through '{foreignKey.Name}', so its rows would be deleted with it or left "
                        + $"pointing at rows that no longer exist. Exclude '{principal}' as well, or stop "
                        + $"excluding '{name}'.");
                }
            }
        }
    }

    private static HashSet<ResetTable> ExcludedTables(IModel model, ResetDataOptions options)
    {
        var excluded = new HashSet<ResetTable>();

        foreach (var clrType in options.ExcludedEntityTypes)
        {
            var entityTypes = model.GetEntityTypes().Where(e => e.ClrType == clrType).ToList();
            if (entityTypes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cannot exclude '{clrType.Name}' from the reset: it is not an entity type in this model.");
            }

            // Every table the type touches, so a TPT or entity-split type is excluded whole.
            excluded.UnionWith(entityTypes
                .SelectMany(e => e.GetTableMappings())
                .Select(m => new ResetTable(m.Table.Schema, m.Table.Name)));
        }

        return excluded;
    }

    private static bool IsExcluded(ITable table, HashSet<ResetTable> excluded, ResetDataOptions options)
        => excluded.Contains(new ResetTable(table.Schema, table.Name))
            || options.ExcludedTables.Any(x =>
                string.Equals(x.Name, table.Name, StringComparison.Ordinal)
                && (x.Schema is null || string.Equals(x.Schema, table.Schema, StringComparison.Ordinal)));

    /// <summary>Tarjan's algorithm, iteratively, over a graph small enough that recursion would do.</summary>
    /// <remarks>
    ///     Iterative anyway: a model with a long foreign-key chain should not be able to overflow the
    ///     test host's stack.
    /// </remarks>
    private static List<List<ResetTable>> StronglyConnectedComponents(
        List<ResetTable> nodes,
        Dictionary<ResetTable, List<ResetTable>> edges)
    {
        var index = new Dictionary<ResetTable, int>();
        var lowLink = new Dictionary<ResetTable, int>();
        var onStack = new HashSet<ResetTable>();
        var stack = new Stack<ResetTable>();
        var components = new List<List<ResetTable>>();
        var next = 0;

        foreach (var root in nodes)
        {
            if (index.ContainsKey(root))
            {
                continue;
            }

            var work = new Stack<(ResetTable Node, int Edge)>();
            work.Push((root, 0));
            index[root] = lowLink[root] = next++;
            stack.Push(root);
            onStack.Add(root);

            while (work.Count > 0)
            {
                var (node, edge) = work.Pop();
                var successors = edges[node];

                if (edge < successors.Count)
                {
                    work.Push((node, edge + 1));
                    var successor = successors[edge];

                    if (!index.TryGetValue(successor, out var successorIndex))
                    {
                        index[successor] = lowLink[successor] = next++;
                        stack.Push(successor);
                        onStack.Add(successor);
                        work.Push((successor, 0));
                    }
                    else if (onStack.Contains(successor))
                    {
                        lowLink[node] = Math.Min(lowLink[node], successorIndex);
                    }

                    continue;
                }

                if (lowLink[node] == index[node])
                {
                    var component = new List<ResetTable>();
                    ResetTable member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != node);

                    components.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                }
            }
        }

        return components;
    }
}
