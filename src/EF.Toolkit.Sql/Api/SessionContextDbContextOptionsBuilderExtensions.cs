using EFToolkit.Sql.Sessions;
using Microsoft.EntityFrameworkCore.Infrastructure;

// Deliberately in EF's own namespace so UseSessionContext() is visible with the using that any EF
// Core application already has.
namespace Microsoft.EntityFrameworkCore;

/// <summary>Configures session context, applied on every connection open, on a <see cref="DbContextOptionsBuilder" />.</summary>
public static class SessionContextDbContextOptionsBuilderExtensions
{
    /// <summary>Sets session-context values every time the context opens a connection.</summary>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="configure">Names the keys and how to resolve their values.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         The values are resolved and sent on each open — <c>set_config</c> on PostgreSQL,
    ///         <c>sp_set_session_context</c> on SQL Server, a user variable on MySQL — in one round trip.
    ///         SQLite has no session context and throws.
    ///     </para>
    ///     <para>
    ///         Calling this again adds to the configuration; a key set twice keeps the later value.
    ///         A connection the application opened itself and handed to the provider is already open, so
    ///         EF never opens it and the values are not applied; let EF own the connection.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     <code>
    ///     services.AddDbContext&lt;AppDb&gt;(o => o
    ///         .UseNpgsql(connectionString)
    ///         .UseSessionContext(s => s.Set("app.tenant_id", sp => sp.GetRequiredService&lt;ITenant&gt;().Id)));
    ///     </code>
    /// </example>
    public static DbContextOptionsBuilder UseSessionContext(
        this DbContextOptionsBuilder optionsBuilder,
        Action<SessionContextBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(configure);

        var existing = optionsBuilder.Options.FindExtension<SessionContextOptionsExtension>();
        var builder = new SessionContextBuilder(existing?.Values ?? []);
        configure(builder);

        // The interceptor reads the values from the options, so it is added once however many times
        // this is called.
        if (existing is null)
        {
            optionsBuilder.AddInterceptors(SessionContextInterceptor.Instance);
        }

        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new SessionContextOptionsExtension([.. builder.Values]));

        return optionsBuilder;
    }

    /// <summary>Sets session-context values every time the context opens a connection.</summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The context options being built.</param>
    /// <param name="configure">Names the keys and how to resolve their values.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseSessionContext<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        Action<SessionContextBuilder> configure)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseSessionContext((DbContextOptionsBuilder)optionsBuilder, configure);
}
