using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.SqlServer;
using EFToolkit.Bulk.SqlServer.Execution;
using EFToolkit.Bulk.SqlServer.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

// Deliberately in EF's own namespace so UseBulkOperations() is visible with the using that any
// EF Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Enables EF.Toolkit.Bulk on a SQL Server <see cref="DbContext" />.
/// </summary>
public static class SqlServerBulkDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Enables EF.Toolkit.Bulk, accelerating <c>SaveChanges()</c> and unlocking the explicit
    ///     <c>BulkInsert</c> / <c>BulkUpdate</c> / <c>BulkMerge</c> / <c>BulkDelete</c> API.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlServer</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseSqlServer(connectionString)
    ///         .UseBulkOperations());
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseBulkOperations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqlServerBulkOptionsBuilder>? configure = null)
    {
        BulkOptionsRegistration.Apply(
            optionsBuilder,
            static options => new SqlServerBulkOptionsBuilder(options),
            configure,
            typeof(SqlServerBulkExecutor));

        optionsBuilder.ReplaceService<
            IModificationCommandBatchFactory,
            SqlServerBulkModificationCommandBatchFactory>();

        return optionsBuilder;
    }

    /// <summary>
    ///     Enables EF.Toolkit.Bulk on a strongly-typed options builder.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlServer</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseBulkOperations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqlServerBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBulkOperations(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for <see cref="UseBulkOperations(DbContextOptionsBuilder, Action{SqlServerBulkOptionsBuilder})" />.
    /// </summary>
    /// <remarks>
    ///     Use this in applications that reference both EF.Toolkit.Bulk.SqlServer and EF.Toolkit.Bulk.PostgreSQL,
    ///     where the shared <c>UseBulkOperations</c> name would be ambiguous.
    /// </remarks>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlServer</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseSqlServerBulk(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqlServerBulkOptionsBuilder>? configure = null)
        => UseBulkOperations(optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for the strongly-typed
    ///     <see cref="UseBulkOperations{TContext}(DbContextOptionsBuilder{TContext}, Action{SqlServerBulkOptionsBuilder})" />.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlServer</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqlServerBulk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqlServerBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseBulkOperations(optionsBuilder, configure);
}
