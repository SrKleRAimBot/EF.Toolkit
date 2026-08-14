using EFToolkit.Audit.Api;

namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Configures where an audit entry's tenant comes from.
/// </summary>
/// <remarks>
///     The two sources compose rather than compete: the entity property is tried first and the
///     provider fills in for entity types that do not carry a tenant of their own. Which is what a
///     real multi-tenant model looks like — most tables are tenant-scoped, a few reference tables
///     are not, and both end up in the same audit trail.
/// </remarks>
public class AuditMultiTenantBuilder
{
    /// <summary>The property name assumed by <see cref="FromEntityProperty()" />.</summary>
    public const string ConventionalPropertyName = "TenantId";

    /// <summary>Initializes a new instance over <paramref name="options" />.</summary>
    /// <param name="options">The settings being built.</param>
    public AuditMultiTenantBuilder(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The settings built so far.</summary>
    public AuditOptions Options { get; protected set; }

    /// <summary>
    ///     Reads the tenant from a <c>TenantId</c> property on the audited entity.
    /// </summary>
    /// <remarks>
    ///     This is the whole Finbuckle.MultiTenant integration. Its <c>IsMultiTenant()</c> adds a
    ///     <c>TenantId</c> shadow property to each multi-tenant type and keeps it filled, and a
    ///     shadow property is readable on the <c>SaveChanges</c> path, so every audited entity
    ///     already carries the tenant that belongs on its audit entry — with no reference to
    ///     Finbuckle from here and nothing else to configure.
    /// </remarks>
    public virtual AuditMultiTenantBuilder FromEntityProperty()
        => FromEntityProperty(ConventionalPropertyName);

    /// <summary>Reads the tenant from a named property on the audited entity.</summary>
    /// <param name="propertyName">The property, shadow or otherwise.</param>
    public virtual AuditMultiTenantBuilder FromEntityProperty(string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        Options = Options with { TenantPropertyName = propertyName };
        return this;
    }

    /// <summary>Resolves the tenant through an application-registered provider.</summary>
    /// <typeparam name="TProvider">The provider, resolved from application services.</typeparam>
    public virtual AuditMultiTenantBuilder FromProvider<TProvider>()
        where TProvider : IAuditTenantProvider
        => From(static (services, cancellationToken) =>
            AuditServiceResolver.Required<TProvider>(services, "MultiTenant(t => t.FromProvider<...>())")
                .GetTenantIdAsync(cancellationToken));

    /// <summary>Resolves the tenant with a delegate over application services.</summary>
    /// <param name="resolver">Reads the tenant from whatever the application registered.</param>
    public virtual AuditMultiTenantBuilder From(
        Func<IServiceProvider, CancellationToken, ValueTask<string?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        Options = Options with { TenantResolver = resolver };
        return this;
    }

    /// <summary>Refuses to write an audit entry whose tenant could not be determined.</summary>
    /// <remarks>
    ///     Worth setting on a genuinely multi-tenant system. An audit row with a null tenant is
    ///     invisible to every tenant-scoped query that will later look for it, which is a leak in
    ///     the direction nobody notices.
    /// </remarks>
    public virtual AuditMultiTenantBuilder Require()
    {
        Options = Options with { RequireTenant = true };
        return this;
    }
}
