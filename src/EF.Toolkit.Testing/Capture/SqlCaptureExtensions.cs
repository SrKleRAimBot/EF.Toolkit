using EFToolkit.Testing;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so these are visible with the using any EF Core application
// already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>SQL capture for contexts built outside the test databases.</summary>
public static class SqlCaptureExtensions
{
    /// <summary>Registers <see cref="SqlCaptureInterceptor" />, so this context's SQL can be captured.</summary>
    /// <param name="optionsBuilder">The options being built.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>Nothing is recorded until a capture is started; see <see cref="CaptureSql" />.</remarks>
    public static DbContextOptionsBuilder UseSqlCapture(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(SqlCaptureInterceptor.Shared);
    }

    /// <summary>Registers <see cref="SqlCaptureInterceptor" />, so this context's SQL can be captured.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options being built.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>Nothing is recorded until a capture is started; see <see cref="CaptureSql" />.</remarks>
    public static DbContextOptionsBuilder<TContext> UseSqlCapture<TContext>(this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        return optionsBuilder.AddInterceptors(SqlCaptureInterceptor.Shared);
    }

    /// <summary>Starts recording the commands this context executes on the current flow.</summary>
    /// <param name="context">
    ///     The context. Must have <see cref="SqlCaptureInterceptor" /> registered — the test databases
    ///     do this for you; elsewhere call <c>UseSqlCapture()</c>.
    /// </param>
    /// <returns>The capture. Dispose it to stop recording.</returns>
    /// <exception cref="InvalidOperationException">The context has no capture interceptor.</exception>
    /// <remarks>
    ///     Commands from other contexts are not recorded, even on the same database. Use
    ///     <c>TestDatabase.CaptureSql()</c> to record every context the test creates.
    /// </remarks>
    public static SqlCapture CaptureSql(this DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var interceptors = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()?.Interceptors;

        if (interceptors?.OfType<SqlCaptureInterceptor>().Any() != true)
        {
            throw new InvalidOperationException(
                $"'{context.GetType().Name}' cannot capture SQL: it has no {nameof(SqlCaptureInterceptor)}. "
                + "Interceptors are fixed when the options are built, so add UseSqlCapture() to the options, "
                + "or create the context from a SqliteTestDatabase or TestDatabase, which register it.");
        }

        return SqlCapture.ForContext(context);
    }
}
