using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Testing;

/// <summary>Constructs a context from options, the way <c>AddDbContext</c> would.</summary>
internal static class ContextActivator<TContext>
    where TContext : DbContext
{
    private static readonly Lazy<Func<DbContextOptions<TContext>, TContext>> Default = new(Resolve);

    /// <summary>A factory for contexts with a constructor taking the options.</summary>
    public static Func<DbContextOptions<TContext>, TContext> Factory => Default.Value;

    private static Func<DbContextOptions<TContext>, TContext> Resolve()
    {
        var type = typeof(TContext);

        // The typed options first: it is what AddDbContext passes, and a context that takes both
        // forms most likely forwards the untyped one for subclasses.
        var constructor = type.GetConstructor([typeof(DbContextOptions<TContext>)])
            ?? type.GetConstructor([typeof(DbContextOptions)]);

        if (constructor is null)
        {
            return _ => throw new InvalidOperationException(
                $"Cannot create '{type.Name}': it has no public constructor taking DbContextOptions<{type.Name}> "
                + "or DbContextOptions, so the test database's options have nowhere to go. Add one, or supply "
                + "a factory with UseContextFactory(options => new " + type.Name + "(...)).");
        }

        return options =>
        {
            try
            {
                return (TContext)constructor.Invoke([options]);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                // The constructor's own exception, not reflection's wrapper around it.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
                throw;
            }
        };
    }
}
