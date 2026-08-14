using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Infrastructure;
using EFToolkit.Audit.PostgreSQL;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseAuditing() is visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Turns on entity auditing for a PostgreSQL context.
/// </summary>
public static class NpgsqlAuditDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Records inserts, updates and deletes of registered entity types into an audit table.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="configure">Optional configuration.</param>
    /// <remarks>
    ///     Call after the provider. That is the whole integration: the audit table is added to the
    ///     model, the capture pipeline is registered, and entity types are registered for auditing
    ///     with <c>IsAudited()</c> in <c>OnModelCreating</c>.
    /// </remarks>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseNpgsql(connectionString)
    ///         .UseAuditing(a => a.Schema("audit")));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<NpgsqlAuditOptionsBuilder>? configure = null)
    {
        AuditOptionsRegistration.Apply(
            optionsBuilder,
            static options => new NpgsqlAuditOptionsBuilder(options),
            configure);

        optionsBuilder.ReplaceService<IModelCustomizer, AuditModelCustomizer>();

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseAuditing(DbContextOptionsBuilder, Action{NpgsqlAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<NpgsqlAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAuditing(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Records inserts, updates and deletes of registered entity types into an audit table.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="configure">Optional configuration.</param>
    /// <remarks>
    ///     Same as <see cref="UseAuditing(DbContextOptionsBuilder, Action{NpgsqlAuditOptionsBuilder})" />,
    ///     under a name that stays unambiguous when both provider packages are referenced.
    /// </remarks>
    public static DbContextOptionsBuilder UseNpgsqlAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<NpgsqlAuditOptionsBuilder>? configure = null)
        => UseAuditing(optionsBuilder, configure);

    /// <inheritdoc cref="UseNpgsqlAuditing(DbContextOptionsBuilder, Action{NpgsqlAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseNpgsqlAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<NpgsqlAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseAuditing(optionsBuilder, configure);
}
