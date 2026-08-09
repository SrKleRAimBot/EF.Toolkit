using EFBulk.Configuration;
using EFBulk.PostgreSQL;
using EFBulk.PostgreSQL.Execution;
using EFBulk.PostgreSQL.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;

// Deliberately in EF's own namespace so UseBulkOperations() is visible with the using that any
// EF Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Enables EF.Bulk on a PostgreSQL <see cref="DbContext" />.
/// </summary>
public static class NpgsqlBulkDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Enables EF.Bulk, accelerating <c>SaveChanges()</c> and unlocking the explicit
    ///     <c>BulkInsert</c> / <c>BulkUpdate</c> / <c>BulkMerge</c> / <c>BulkDelete</c> API.
    /// </summary>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseNpgsql</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseNpgsql(connectionString)
    ///         .UseBulkOperations());
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseBulkOperations(
        this DbContextOptionsBuilder optionsBuilder,
        Action<NpgsqlBulkOptionsBuilder>? configure = null)
    {
        BulkOptionsRegistration.Apply(
            optionsBuilder,
            static options => new NpgsqlBulkOptionsBuilder(options),
            configure,
            typeof(NpgsqlBulkExecutor),
            [typeof(NpgsqlSequenceResolver)]);

        optionsBuilder.ReplaceService<
            IModificationCommandBatchFactory,
            NpgsqlBulkModificationCommandBatchFactory>();

        return optionsBuilder;
    }

    /// <summary>
    ///     Enables EF.Bulk on a strongly-typed options builder.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseNpgsql</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseBulkOperations<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<NpgsqlBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBulkOperations(
            (DbContextOptionsBuilder)optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for <see cref="UseBulkOperations(DbContextOptionsBuilder, Action{NpgsqlBulkOptionsBuilder})" />.
    /// </summary>
    /// <remarks>
    ///     Use this in applications that reference both EF.Bulk.PostgreSQL and EF.Bulk.SqlServer,
    ///     where the shared <c>UseBulkOperations</c> name would be ambiguous.
    /// </remarks>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseNpgsql</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseNpgsqlBulk(
        this DbContextOptionsBuilder optionsBuilder,
        Action<NpgsqlBulkOptionsBuilder>? configure = null)
        => UseBulkOperations(optionsBuilder, configure);

    /// <summary>
    ///     Unambiguous alias for the strongly-typed
    ///     <see cref="UseBulkOperations{TContext}(DbContextOptionsBuilder{TContext}, Action{NpgsqlBulkOptionsBuilder})" />.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The builder being configured. Call after <c>UseNpgsql</c>.</param>
    /// <param name="configure">Optional settings.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseNpgsqlBulk<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<NpgsqlBulkOptionsBuilder>? configure = null)
        where TContext : DbContext
        => UseBulkOperations(optionsBuilder, configure);
}
