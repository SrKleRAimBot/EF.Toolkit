using EFToolkit.Audit.Bulk;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseBulkAuditing() is visible with the using that any EF
// Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>
///     Joins EF.Toolkit.Audit to EF.Toolkit.Bulk.
/// </summary>
public static class BulkAuditingDbContextOptionsBuilderExtensions
{
    /// <summary>
    ///     Audits the explicit bulk API, and writes audit entries in bulk.
    /// </summary>
    /// <param name="optionsBuilder">The context options builder.</param>
    /// <remarks>
    ///     <para>
    ///         Call after both <c>UseBulkOperations()</c> and <c>UseAuditing()</c>. Neither package
    ///         references the other, so this one line is what makes them work together — and it
    ///         throws at startup if either is missing, so it cannot be forgotten quietly.
    ///     </para>
    ///     <para>
    ///         Two things change. <c>BulkInsertAsync</c> and its siblings start producing audit
    ///         entries, which they otherwise cannot: they bypass the change tracker, so no
    ///         <c>SaveChanges</c> interceptor sees them. And the entries themselves are written
    ///         through a bulk copy once there are enough of them, which matters because auditing a
    ///         hundred-thousand-row operation produces a hundred thousand entries.
    ///     </para>
    ///     <para>
    ///         Transparent <c>SaveChanges()</c> acceleration needs none of this. It replaces a
    ///         service below <c>SaveChanges</c>, so it is already audited by <c>UseAuditing()</c>
    ///         alone.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseNpgsql(connectionString)
    ///         .UseBulkOperations()
    ///         .UseAuditing(a => a.Schema("audit"))
    ///         .UseBulkAuditing());
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseBulkAuditing(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new BulkAuditingOptionsExtension());

        return optionsBuilder;
    }

    /// <inheritdoc cref="UseBulkAuditing(DbContextOptionsBuilder)" />
    /// <typeparam name="TContext">The context type.</typeparam>
    public static DbContextOptionsBuilder<TContext> UseBulkAuditing<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseBulkAuditing((DbContextOptionsBuilder)optionsBuilder);
}
