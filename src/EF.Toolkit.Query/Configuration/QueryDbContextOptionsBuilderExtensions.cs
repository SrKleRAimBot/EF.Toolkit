using EFToolkit.Query.Configuration;
using EFToolkit.Query.Tracking;
using Microsoft.EntityFrameworkCore.Query;

// Deliberately in EF's own namespace so UseQueryHelpers() is visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures EF.Toolkit.Query on a <see cref="DbContextOptionsBuilder" />.</summary>
/// <remarks>
///     Unlike EF.Toolkit.Bulk and EF.Toolkit.Audit, this entry point lives in the core package. Those
///     two had to push theirs into provider packages because the services they replace derive from
///     provider-specific types; nothing here does, so there is one package and no provider variant to
///     install.
/// </remarks>
public static class QueryDbContextOptionsBuilderExtensions
{
    /// <summary>Enables the EF.Toolkit.Query helpers on this context.</summary>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="configure">Optional configuration. Omit for defaults.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;ShopContext&gt;(options => options
    ///         .UseNpgsql(connectionString)
    ///         .UseQueryHelpers(q => q.DefaultPageSize(25).MaxPageSize(200)));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseQueryHelpers(
        this DbContextOptionsBuilder optionsBuilder,
        Action<QueryOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        var options = QueryOptionsRegistration.Apply(
            optionsBuilder,
            static o => new QueryOptionsBuilder(o),
            configure);

        if (options.TrackingScopes)
        {
            optionsBuilder.ReplaceService<IQueryContextFactory, TrackingScopeQueryContextFactory>();
        }

        return optionsBuilder;
    }

    /// <summary>Enables the EF.Toolkit.Query helpers on this context.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="configure">Optional configuration. Omit for defaults.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseQueryHelpers<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<QueryOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseQueryHelpers(
            (DbContextOptionsBuilder)optionsBuilder,
            configure);
}
