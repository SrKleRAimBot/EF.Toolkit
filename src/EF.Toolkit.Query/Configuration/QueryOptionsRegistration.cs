using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Query.Configuration;

/// <summary>
///     Builds <see cref="QueryOptions" /> from a configuration delegate and attaches them to a
///     <see cref="DbContextOptionsBuilder" />.
/// </summary>
/// <remarks>
///     Shared by the <c>UseQueryHelpers</c> overloads so the generic and non-generic forms cannot
///     drift apart. The builder type is generic so a derived builder — should a future provider
///     package add provider-specific settings — flows through unchanged.
/// </remarks>
public static class QueryOptionsRegistration
{
    /// <summary>Applies configuration and registers the resulting options with the context.</summary>
    /// <typeparam name="TBuilder">The builder type handed to <paramref name="configure" />.</typeparam>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="createBuilder">Creates the builder over the starting options.</param>
    /// <param name="configure">The caller's configuration, or <see langword="null" /> for defaults.</param>
    /// <returns>The settings that were registered.</returns>
    public static QueryOptions Apply<TBuilder>(
        DbContextOptionsBuilder optionsBuilder,
        Func<QueryOptions, TBuilder> createBuilder,
        Action<TBuilder>? configure)
        where TBuilder : QueryOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(createBuilder);

        var builder = createBuilder(QueryOptions.Default);
        configure?.Invoke(builder);

        var extension = new QueryOptionsExtension(builder.Options);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return builder.Options;
    }
}
