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
        bool hasExclusions,
        IReadOnlyList<ResetTable>? retained = null,
        IReadOnlyList<(string Name, string? Schema)>? excludedNames = null)
    {
        Tables = tables;
        CyclicTables = cyclicTables;
        HasExclusions = hasExclusions;
        Retained = retained ?? [];
        ExcludedNames = excludedNames ?? [];
    }

    /// <summary>
    ///     The names passed to <c>ExcludeTable</c>, as given — a null schema matching any. Kept apart from
    ///     <see cref="Retained" /> because they need not be in the model: a migration-created partition
    ///     the caller wants left alone is exactly that.
    /// </summary>
    public IReadOnlyList<(string Name, string? Schema)> ExcludedNames { get; }

    /// <summary>Tables the model maps that this reset leaves alone — the excluded ones.</summary>
    public IReadOnlyList<ResetTable> Retained { get; }

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

        var retained = relational.Tables
            .Select(t => new ResetTable(t.Schema, t.Name))
            .Where(t => !included.ContainsKey(t))
            .ToList();

        return new ResetPlan(ordered, cyclic, options.HasExclusions, retained, options.ExcludedTables);
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
        bool hasExclusions = false,
        IReadOnlyList<ResetTable>? retained = null,
        IReadOnlyList<(string Name, string? Schema)>? excludedNames = null)
        => new(tables, new HashSet<ResetTable>(cyclicTables ?? []), hasExclusions, retained, excludedNames);

    /// <summary>
    ///     Refuses a SQL Server reset of a table an enabled row-level-security filter applies to, from rows
    ///     produced by <see cref="DatabaseForeignKeys.SqlServerFilteredTables" />.
    /// </summary>
    internal static void RefuseFilteredTables(IEnumerable<DatabaseReference> filtered, bool canSeePolicies = true)
    {
        if (!canSeePolicies)
        {
            throw new InvalidOperationException(
                "Cannot reset: this login cannot see the database's row-level security policies, so it cannot "
                + "tell whether one filters a table being reset — and a filter applies to DELETE, which would "
                + "then remove only the rows this session can see and report success. Run the reset as a login "
                + "with VIEW DEFINITION on the database (db_owner has it), or GRANT VIEW DEFINITION TO it.");
        }

        if (filtered.FirstOrDefault() is { } table)
        {
            throw new InvalidOperationException(
                $"Cannot reset '{table.Table}': the security policy '{table.ReferencedTable}' filters it, and a "
                + "filter applies to DELETE as well — the reset would remove only the rows this session can see "
                + "and report success with the rest still there. Disable the policy for the reset "
                + "(ALTER SECURITY POLICY ... WITH (STATE = OFF)), or exclude the table.");
        }
    }

    /// <summary>
    ///     Refuses a PostgreSQL reset whose <c>TRUNCATE</c> would reach a table it was not asked to
    ///     empty, from rows produced by <see cref="DatabaseForeignKeys.PostgreSqlDescendants" />.
    /// </summary>
    internal static void RefuseTruncatedDescendants(IEnumerable<DatabaseReference> descendants)
    {
        var rows = descendants.ToList();

        // A row naming one table twice is a kept table the reset would empty all the same: excluded
        // under one mapping's name, reset under another's that the database resolves to the same table.
        if (rows.FirstOrDefault(r => r.Table == r.ReferencedTable) is { } both)
        {
            throw KeptAndReset(both.Table);
        }

        if (rows.FirstOrDefault() is { } descendant)
        {
            throw new InvalidOperationException(
                $"Cannot reset '{descendant.ReferencedTable}': PostgreSQL truncates every table that inherits "
                + $"from it, and '{descendant.Table}' does but is not part of the reset — an unmapped child "
                + "table, or a partition the reset was told to keep. Its rows would be emptied too. Map "
                + "the table, stop excluding it, or exclude its parent as well.");
        }
    }

    /// <summary>
    ///     SQL Server: refuses when a kept table is also a reset one, from rows of
    ///     <see cref="DatabaseForeignKeys.SqlServerKeptAndReset" /> — the pairs whose names
    ///     <c>OBJECT_ID</c> resolves to one object.
    /// </summary>
    internal static void RefuseKeptAndReset(IEnumerable<DatabaseReference> overlaps)
    {
        if (overlaps.FirstOrDefault() is { } overlap)
        {
            throw KeptAndReset(overlap.Table);
        }
    }

    /// <summary>
    ///     SQLite: refuses when a kept table is also a reset one. The reset deletes from <c>main</c> whatever
    ///     schema a mapping names (EF's SQLite provider has none), and SQLite compares identifiers ignoring
    ///     the case of ASCII letters — so <c>Settings</c>, <c>settings</c> and <c>app.settings</c> are all
    ///     <c>main.settings</c> to it, and are compared here the same way, by name alone.
    /// </summary>
    internal void RefuseSqliteKeptAndReset()
    {
        static string Identity(ResetTable table) => SqliteFold(table.Name);

        var reset = Tables.Select(Identity).ToHashSet(StringComparer.Ordinal);
        if (Retained.FirstOrDefault(t => reset.Contains(Identity(t))) is { } kept)
        {
            throw KeptAndReset(kept.Schema is null ? kept.Name : kept.Schema + "." + kept.Name);
        }
    }

    /// <summary>
    ///     MySQL: refuses when a kept table is also a reset one. A name without a schema is the connection's
    ///     database, and with <c>lower_case_table_names</c> 1 or 2 names compare ignoring case — so
    ///     <c>Settings</c> and <c>shop.settings</c> can be one table.
    /// </summary>
    /// <param name="database">The connection's current database, for names without one.</param>
    /// <param name="ignoreCase">Whether the server compares table names ignoring case.</param>
    internal void RefuseMySqlKeptAndReset(string? database, bool ignoreCase)
    {
        // A MySQL reset empties the connection's own database: the checks that decide what may be deleted
        // — the storage engine that would ignore a rollback, views — read that database only, so a table
        // mapped into another one would be deleted from unchecked. It is refused instead. (Foreign keys are
        // read across every database the login can see.)
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Tables.FirstOrDefault(t => t.Schema is not null && !string.Equals(t.Schema, database, comparison)) is { } elsewhere)
        {
            throw new InvalidOperationException(
                $"Cannot reset '{elsewhere.Schema}.{elsewhere.Name}': it is in another database than the "
                + $"connection's ('{database}'), and the checks a MySQL reset makes before deleting — whether a "
                + "table ignores a rollback, whether it is a view — read the connection's database only. "
                + "Exclude it, or reset it through a context connected to that database.");
        }

        string Identity(ResetTable table)
        {
            var identity = (table.Schema ?? database ?? "") + "." + table.Name;
            return ignoreCase ? identity.ToLowerInvariant() : identity;
        }

        var reset = Tables.Select(Identity).ToHashSet(StringComparer.Ordinal);
        if (Retained.FirstOrDefault(t => reset.Contains(Identity(t))) is { } kept)
        {
            throw KeptAndReset(kept.Schema is null ? kept.Name : kept.Schema + "." + kept.Name);
        }
    }

    // SQLite folds only A-Z: É and é stay two names.
    private static string SqliteFold(string identifier)
        => string.Create(identifier.Length, identifier, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
            }
        });

    // Two mappings that name one table differently are one table to the database: excluded under one,
    // the reset would still empty it under the other.
    private static InvalidOperationException KeptAndReset(string table)
        => new(
            $"Cannot reset '{table}': it was excluded, but another mapping names the same table differently "
            + "— with its schema written out where the excluded one leaves it to the default, say, or in "
            + "another case — and would empty it. Exclude that mapping as well, or map both the same way.");

    /// <summary>
    ///     A copy of this plan with every unqualified table given the schema the database resolved it to,
    ///     from rows produced by <see cref="DatabaseForeignKeys.ResolveSchemas" />.
    /// </summary>
    internal ResetPlan WithResolvedSchemas(IEnumerable<DatabaseReference> resolved)
    {
        // Keyed by the names this plan itself put in the query, so exact comparison is right whatever
        // the server's own rule.
        var schemas = resolved
            .Where(r => !string.IsNullOrEmpty(r.TableSchema))
            .ToDictionary(r => r.Table, r => r.TableSchema!, StringComparer.Ordinal);

        ResetTable Qualify(ResetTable table)
            => table.Schema is null && schemas.TryGetValue(table.Name, out var schema) ? table with { Schema = schema } : table;

        return new ResetPlan(
            [.. Tables.Select(Qualify)], new HashSet<ResetTable>(CyclicTables.Select(Qualify)), HasExclusions, Retained, ExcludedNames);
    }

    /// <summary>
    ///     Refuses the reset if the catalog reported any foreign key from a table outside it into one it
    ///     empties — rows from <see cref="DatabaseForeignKeys.Query" />, which has already decided, by the
    ///     database's own rules, which tables are outside.
    /// </summary>
    internal static void RefuseUnplannedReferences(IEnumerable<DatabaseReference> references)
    {
        if (references.FirstOrDefault() is { } reference)
        {
            // A dependent in another database cannot be reset with this one, so mapping it is no way out.
            var remedy = reference.OutsideDatabase
                ? "It is in another database, which a reset through this connection cannot empty: exclude "
                    + $"'{reference.ReferencedTable}' from the reset, or remove the foreign key."
                : "Map the table in the model so the reset empties it too, or remove the foreign key.";
            throw new InvalidOperationException(
                $"Cannot reset '{reference.ReferencedTable}': '{reference.Table}', which the reset does not "
                + "empty, references it. Emptying the referenced table would, depending on the foreign key, "
                + "fail on it, delete or update rows outside the reset (ON DELETE CASCADE, SET NULL, SET "
                + "DEFAULT), or — where the reset suspends or defers the checks — leave them pointing at rows "
                + "that no longer exist. " + remedy);
        }
    }

    // A table left out of the reset that references one being emptied cannot keep its rows intact. Its
    // ON DELETE action still runs whatever the enforcement mode — CASCADE deletes them, SET NULL and SET
    // DEFAULT rewrite them — and otherwise the foreign key either refuses the delete or, where the reset
    // suspends or defers the checks (SQLite always, the other engines on a foreign-key cycle), leaves
    // them pointing at rows that no longer exist. None of that is what "exclude this table" asks for,
    // so the plan refuses it.
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
                        + $"through '{foreignKey.Name}', so its rows would be deleted or updated with it, the "
                        + "reset refused, or the rows left pointing at rows that no longer exist. Exclude "
                        + $"'{principal}' as well, or stop excluding '{name}'.");
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

    // A model table with no schema lives in whatever schema the connection defaults to, which the model
    // cannot know, so an exclusion that names a schema still matches it. Erring that way can only keep
    // rows; the other way, ExcludeTable("EventsArchive", "public") reset the very table it named.
    private static bool IsExcluded(ITable table, HashSet<ResetTable> excluded, ResetDataOptions options)
        => excluded.Contains(new ResetTable(table.Schema, table.Name))
            || options.ExcludedTables.Any(x =>
                string.Equals(x.Name, table.Name, StringComparison.Ordinal)
                && (x.Schema is null || table.Schema is null || string.Equals(x.Schema, table.Schema, StringComparison.Ordinal)));

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
