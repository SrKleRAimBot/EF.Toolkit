using EFToolkit.Conventions.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseEntityConventions() is visible with the using that any EF
// Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures EF.Toolkit.Conventions on a <see cref="DbContextOptionsBuilder" />.</summary>
public static class ConventionsDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Applies the soft-delete and timestamp conventions the model declares on every save.
    /// </summary>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="configure">Optional configuration. Omit for defaults.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;BlogContext&gt;(o => o
    ///         .UseNpgsql(connectionString)
    ///         .UseEntityConventions(c => c.Clock(timeProvider)));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseEntityConventions(
        this DbContextOptionsBuilder optionsBuilder,
        Action<ConventionsOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        // Calling this twice amends the first call rather than adding a second interceptor, which
        // would stamp every row twice and convert nothing the second time.
        var existing = optionsBuilder.Options.FindExtension<ConventionsOptionsExtension>()?.Options
            ?? ConventionsOptions.Default;

        var builder = new ConventionsOptionsBuilder(existing);
        configure?.Invoke(builder);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new ConventionsOptionsExtension(builder.Options));

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseEntityConventions(DbContextOptionsBuilder, Action{ConventionsOptionsBuilder}?)" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseEntityConventions<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<ConventionsOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseEntityConventions((DbContextOptionsBuilder)optionsBuilder, configure);
}
