using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Infrastructure;
using EFToolkit.Audit.SqlServer;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseAuditing() is visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Turns on entity auditing for a SQL Server context.
/// </summary>
public static class SqlServerAuditDbContextOptionsBuilderExtensions
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
    ///         .UseSqlServer(connectionString)
    ///         .UseAuditing(a => a.Schema("audit")));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqlServerAuditOptionsBuilder>? configure = null)
    {
        AuditOptionsRegistration.Apply(
            optionsBuilder,
            static options => new SqlServerAuditOptionsBuilder(options),
            configure);

        optionsBuilder.ReplaceService<IModelCustomizer, AuditModelCustomizer>();

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseAuditing(DbContextOptionsBuilder, Action{SqlServerAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqlServerAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAuditing(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Records inserts, updates and deletes of registered entity types into an audit table.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="configure">Optional configuration.</param>
    /// <remarks>
    ///     Same as <see cref="UseAuditing(DbContextOptionsBuilder, Action{SqlServerAuditOptionsBuilder})" />,
    ///     under a name that stays unambiguous when both provider packages are referenced.
    /// </remarks>
    public static DbContextOptionsBuilder UseSqlServerAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqlServerAuditOptionsBuilder>? configure = null)
        => UseAuditing(optionsBuilder, configure);

    /// <inheritdoc cref="UseSqlServerAuditing(DbContextOptionsBuilder, Action{SqlServerAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseSqlServerAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqlServerAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseAuditing(optionsBuilder, configure);
}
