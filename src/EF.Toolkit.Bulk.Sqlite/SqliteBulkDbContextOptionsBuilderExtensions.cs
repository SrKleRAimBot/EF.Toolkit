using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Sqlite;
using EFToolkit.Bulk.Sqlite.Execution;
using EFToolkit.Bulk.Sqlite.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

// Deliberately in EF's own namespace so UseBulkOperations() is visible with the using that any
// EF Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Enables EF.Toolkit.Bulk on a SQLite <see cref="DbContext" />.
/// </summary>
public static class SqliteBulkDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Enables EF.Toolkit.Bulk, accelerating <c>SaveChanges()</c> and unlocking the explicit
    ///     <c>BulkInsert</c> / <c>BulkUpdate</c> / <c>BulkMerge</c> / <c>BulkDelete</c> API.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlite</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseSqlite(connectionString)
    ///         .UseBulkOperations());
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseBulkOperations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqliteBulkOptionsBuilder>? configure = null)
    {
        BulkOptionsRegistration.Apply(
            optionsBuilder,
            static options => new SqliteBulkOptionsBuilder(options),
            configure,
            typeof(SqliteBulkExecutor));

        optionsBuilder.ReplaceService<
            IModificationCommandBatchFactory,
            SqliteBulkModificationCommandBatchFactory>();

        return optionsBuilder;
    }

    /// <summary>
    ///     Enables EF.Toolkit.Bulk on a strongly-typed options builder.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlite</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseBulkOperations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqliteBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBulkOperations(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for <see cref="UseBulkOperations(DbContextOptionsBuilder, Action{SqliteBulkOptionsBuilder})" />.
    /// </summary>
    /// <remarks>
    ///     Use this in applications that reference more than one EF.Toolkit.Bulk provider package,
    ///     where the shared <c>UseBulkOperations</c> name would be ambiguous.
    /// </remarks>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlite</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseSqliteBulk(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SqliteBulkOptionsBuilder>? configure = null)
        => UseBulkOperations(optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for the strongly-typed
    ///     <see cref="UseBulkOperations{TContext}(DbContextOptionsBuilder{TContext}, Action{SqliteBulkOptionsBuilder})" />.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseSqlite</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteBulk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SqliteBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseBulkOperations(optionsBuilder, configure);
}
