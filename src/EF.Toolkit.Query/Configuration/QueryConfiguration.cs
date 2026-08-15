using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Query.Configuration;

/// <summary>Reads the EF.Toolkit.Query settings off a configured context.</summary>
internal static class QueryConfiguration
{
    /// <summary>
    ///     The settings established by <c>UseQueryHelpers()</c>, or a teaching error naming the call
    ///     that is missing.
    /// </summary>
    /// <remarks>
    ///     Read from the options extension rather than from EF's internal service provider, so that a
    ///     context which was never configured produces this message instead of EF's generic
    ///     "no service for type" one.
    /// </remarks>
    internal static QueryOptions Required(DbContext context, string call)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetService<IDbContextOptions>().FindExtension<QueryOptionsExtension>()?.Options
            ?? throw new QueryNotSupportedException(
                $"EF.Toolkit.Query is not configured for this context, so {call} has no settings to "
                + "read. Add UseQueryHelpers() alongside your provider, for example: "
                + "options.UseNpgsql(connectionString).UseQueryHelpers(). The overloads that do not "
                + "take a DbContext work without configuration and use QueryOptions.Default.");
    }
}
