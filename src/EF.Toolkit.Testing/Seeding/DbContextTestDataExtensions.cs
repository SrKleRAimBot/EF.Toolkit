using EFToolkit.Testing;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Model-driven test data, through the context the test already has.</summary>
/// <remarks>
///     Uses the <see cref="TestData" /> of the test database that created the context, so values are
///     deterministic per database; for any other context, <see cref="TestData.Shared" />.
/// </remarks>
public static class DbContextTestDataExtensions
{
    /// <summary>Builds a <typeparamref name="TEntity" /> with every required property set. Nothing is saved.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context whose model describes the entity.</param>
    /// <param name="customize">Applied last, to set the values the test cares about.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <returns>The entity, with its required principals attached.</returns>
    /// <example>
    ///     <code>
    ///     var order = context.Build&lt;Order&gt;(o => o.Total = 0m);
    ///     order.Customer;   // built too: the reference is required
    ///     </code>
    /// </example>
    public static TEntity Build<TEntity>(this DbContext context, Action<TEntity>? customize = null, bool fillOptional = false)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return TestData.For(context).Build(context, customize, fillOptional);
    }

    /// <summary>Builds <paramref name="count" /> entities. Nothing is saved.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context whose model describes the entity.</param>
    /// <param name="count">How many to build.</param>
    /// <param name="customize">Applied last to each, with its zero-based index.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <returns>The entities.</returns>
    public static IReadOnlyList<TEntity> BuildMany<TEntity>(
        this DbContext context,
        int count,
        Action<TEntity, int>? customize = null,
        bool fillOptional = false)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return TestData.For(context).BuildMany(context, count, customize, fillOptional);
    }

    /// <summary>Builds a <typeparamref name="TEntity" /> and inserts it with its principals.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context to insert through. Its change tracker is cleared afterwards.</param>
    /// <param name="customize">Applied last, to set the values the test cares about.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The entity, with store-generated keys populated.</returns>
    public static Task<TEntity> CreateAsync<TEntity>(
        this DbContext context,
        Action<TEntity>? customize = null,
        bool fillOptional = false,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return TestData.For(context).CreateAsync(context, customize, fillOptional, cancellationToken);
    }

    /// <summary>Builds <paramref name="count" /> entities and inserts them with their principals.</summary>
    /// <typeparam name="TEntity">A mapped entity type.</typeparam>
    /// <param name="context">The context to insert through. Its change tracker is cleared afterwards.</param>
    /// <param name="count">How many to build.</param>
    /// <param name="customize">Applied last to each, with its zero-based index.</param>
    /// <param name="fillOptional">Also fill nullable properties and optional owned references.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The entities, with store-generated keys populated.</returns>
    public static Task<IReadOnlyList<TEntity>> CreateManyAsync<TEntity>(
        this DbContext context,
        int count,
        Action<TEntity, int>? customize = null,
        bool fillOptional = false,
        CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(context);
        return TestData.For(context).CreateManyAsync(context, count, customize, fillOptional, cancellationToken);
    }
}
