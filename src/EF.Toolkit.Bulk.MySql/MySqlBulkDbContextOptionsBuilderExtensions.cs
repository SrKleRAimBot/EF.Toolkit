using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.MySql;
using EFToolkit.Bulk.MySql.Execution;
using EFToolkit.Bulk.MySql.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseBulkOperations() is visible with the using that any
// EF Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Enables EF.Toolkit.Bulk on a MySQL <see cref="DbContext" />.
/// </summary>
public static class MySqlBulkDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Enables EF.Toolkit.Bulk, accelerating <c>SaveChanges()</c> and unlocking the explicit
    ///     <c>BulkInsert</c> / <c>BulkUpdate</c> / <c>BulkMerge</c> / <c>BulkDelete</c> API.
    /// </summary>
    /// <param name="optionsBuilder">
    ///     The builder being configured. Call after the MySQL provider — <c>UseMySQL</c> or
    ///     <c>UseMySql</c> — because it is the provider's own batching that is extended.
    /// </param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseMySQL(connectionString)
    ///         .UseBulkOperations());
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseBulkOperations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<MySqlBulkOptionsBuilder>? configure = null)
    {
        BulkOptionsRegistration.Apply(
            optionsBuilder,
            static options => new MySqlBulkOptionsBuilder(options),
            configure,
            typeof(MySqlBulkExecutor),
            [typeof(MySqlAutoIncrementResolver)]);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new MySqlBulkBatchingExtension());

        return optionsBuilder;
    }

    /// <summary>
    ///     Enables EF.Toolkit.Bulk on a strongly-typed options builder.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after the MySQL provider.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseBulkOperations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<MySqlBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBulkOperations(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for <see cref="UseBulkOperations(DbContextOptionsBuilder, Action{MySqlBulkOptionsBuilder})" />.
    /// </summary>
    /// <remarks>
    ///     Use this in applications that reference more than one EF.Toolkit.Bulk provider package,
    ///     where the shared <c>UseBulkOperations</c> name would be ambiguous.
    /// </remarks>
    /// <param name="optionsBuilder">The builder being configured. Call after the MySQL provider.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseMySqlBulk(
        this DbContextOptionsBuilder optionsBuilder,
        Action<MySqlBulkOptionsBuilder>? configure = null)
        => UseBulkOperations(optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for the strongly-typed
    ///     <see cref="UseBulkOperations{TContext}(DbContextOptionsBuilder{TContext}, Action{MySqlBulkOptionsBuilder})" />.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after the MySQL provider.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseMySqlBulk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<MySqlBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseBulkOperations(optionsBuilder, configure);
}
