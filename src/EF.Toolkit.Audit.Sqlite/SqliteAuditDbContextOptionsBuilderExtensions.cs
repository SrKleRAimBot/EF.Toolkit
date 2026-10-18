using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Infrastructure;
using EFToolkit.Audit.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseAuditing() is visible with the using that any EF Core
// application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Turns on entity auditing for a SQLite context.
/// </summary>
public static class SqliteAuditDbContextOptionsBuilderExtensions
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
    ///         .UseSqlite(connectionString)
    ///         .UseAuditing(a => a.Schema("audit")));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqliteAuditOptionsBuilder>? configure = null)
    {
        AuditOptionsRegistration.Apply(
            optionsBuilder,
            static options => new SqliteAuditOptionsBuilder(options),
            configure);

        optionsBuilder.ReplaceService<IModelCustomizer, AuditModelCustomizer>();

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseAuditing(DbContextOptionsBuilder, Action{SqliteAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqliteAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseAuditing(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Records inserts, updates and deletes of registered entity types into an audit table.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <param name="configure">Optional configuration.</param>
    /// <remarks>
    ///     Same as <see cref="UseAuditing(DbContextOptionsBuilder, Action{SqliteAuditOptionsBuilder})" />,
    ///     under a name that stays unambiguous when more than one provider package is referenced.
    /// </remarks>
    public static DbContextOptionsBuilder UseSqliteAuditing(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqliteAuditOptionsBuilder>? configure = null)
        => UseAuditing(optionsBuilder, configure);

    /// <inheritdoc cref="UseSqliteAuditing(DbContextOptionsBuilder, Action{SqliteAuditOptionsBuilder})" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseSqliteAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqliteAuditOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseAuditing(optionsBuilder, configure);
}
