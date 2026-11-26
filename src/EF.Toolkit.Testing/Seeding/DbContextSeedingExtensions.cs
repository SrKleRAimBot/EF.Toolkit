// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Arranges data for a test.</summary>
/// <remarks>
///     <para>
///         Each call adds the entities with their graphs, saves, and then <b>clears the change
///         tracker</b>. That last step is the point. Left tracked, the seeded instances sit in the
///         context's identity map, and a later query in the same context hands those very instances
///         back instead of reading the rows — so an assertion meant to check what the code under test
///         wrote to the database checks what the test itself put in memory, and passes either way.
///     </para>
///     <para>
///         The whole tracker is cleared, not only the seeded entities, and anything else pending on
///         the context is saved with them. Seed before acting, not halfway through.
///     </para>
/// </remarks>
public static class DbContextSeedingExtensions
{
    /// <summary>Inserts <paramref name="entities" /> and their graphs, then clears the change tracker.</summary>
    /// <param name="context">The context.</param>
    /// <param name="entities">Entities of any mapped types.</param>
    /// <returns>The same instances, with store-generated keys populated.</returns>
    /// <example>
    ///     <code>
    ///     var (alice, order) = (new Customer { Name = "Alice" }, new Order { Total = 10 });
    ///     await context.SeedAsync(alice, order);
    ///     alice.Id;   // populated
    ///     </code>
    /// </example>
    public static async Task<IReadOnlyList<object>> SeedAsync(this DbContext context, params object[] entities)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        await SaveAndClearAsync(context, c => c.AddRange(entities), CancellationToken.None).ConfigureAwait(false);
        return entities;
    }

    /// <summary>Inserts <paramref name="entities" /> and their graphs, then clears the change tracker.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="entities">The entities.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The same instances, with store-generated keys populated.</returns>
    public static async Task<IReadOnlyList<TEntity>> SeedAsync<TEntity>(
        this DbContext context,
        IEnumerable<TEntity> entities,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var materialized = entities as IReadOnlyList<TEntity> ?? [.. entities];
        await SaveAndClearAsync(context, c => c.AddRange(materialized), cancellationToken).ConfigureAwait(false);
        return materialized;
    }

    /// <summary>Inserts <paramref name="entity" /> and its graph, then clears the change tracker.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The same instance, with store-generated keys populated.</returns>
    public static async Task<TEntity> SeedOneAsync<TEntity>(
        this DbContext context,
        TEntity entity,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entity);

        await SaveAndClearAsync(context, c => c.Add(entity), cancellationToken).ConfigureAwait(false);
        return entity;
    }

    /// <summary>Inserts <paramref name="entities" /> and their graphs, then clears the change tracker.</summary>
    /// <param name="context">The context.</param>
    /// <param name="entities">Entities of any mapped types.</param>
    /// <returns>The same instances, with store-generated keys populated.</returns>
    public static IReadOnlyList<object> Seed(this DbContext context, params object[] entities)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        SaveAndClear(context, c => c.AddRange(entities));
        return entities;
    }

    /// <summary>Inserts <paramref name="entities" /> and their graphs, then clears the change tracker.</summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="context">The context.</param>
    /// <param name="entities">The entities.</param>
    /// <returns>The same instances, with store-generated keys populated.</returns>
    public static IReadOnlyList<TEntity> Seed<TEntity>(this DbContext context, IEnumerable<TEntity> entities)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);

        var materialized = entities as IReadOnlyList<TEntity> ?? [.. entities];
        SaveAndClear(context, c => c.AddRange(materialized));
        return materialized;
    }

    // Attaching sits inside the try too: AddRange tracks the entities one by one and throws part way on a
    // duplicate key, which left the ones before it tracked for the test's next SaveChanges to insert.
    private static async Task SaveAndClearAsync(DbContext context, Action<DbContext> add, CancellationToken cancellationToken)
    {
        try
        {
            add(context);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Cleared on failure too: a half-added graph left tracked would be retried by the next
            // SaveChanges the test makes, and fail it with an error about the seed, not the test.
            context.ChangeTracker.Clear();
        }
    }

    private static void SaveAndClear(DbContext context, Action<DbContext> add)
    {
        try
        {
            add(context);
            context.SaveChanges();
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }
}
