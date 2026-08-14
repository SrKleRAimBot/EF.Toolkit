namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Resolves application services for the actor, tenant and identifier providers.
/// </summary>
/// <remarks>
///     These providers belong to the application, not to EF Core. An actor provider that reads the
///     current user needs <c>IHttpContextAccessor</c>; an identifier provider needs whatever
///     generates identifiers elsewhere in the codebase. None of that is registered in EF's internal
///     service provider, and none of it should be — so the resolution goes through the application
///     provider EF carries on its options, and says so clearly when there is none.
/// </remarks>
internal static class AuditServiceResolver
{
    public static T Required<T>(IServiceProvider? services, string call)
        where T : notnull
    {
        if (services is null)
        {
            throw new AuditNotSupportedException(
                $"{call} needs '{typeof(T).Name}' from the application's service provider, and this "
                + "DbContext was not configured with one. Register the context with "
                + "AddDbContext/AddDbContextPool, or supply the value with a delegate overload "
                + "instead.");
        }

        return (T?)services.GetService(typeof(T))
            ?? throw new AuditNotSupportedException(
                $"{call} needs '{typeof(T).Name}', which is not registered in the application's "
                + "service provider.");
    }
}
