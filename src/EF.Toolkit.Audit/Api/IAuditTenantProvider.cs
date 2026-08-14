namespace EFToolkit.Audit.Api;

/// <summary>
///     Supplies the tenant for audit entries when the audited entity does not carry one itself.
/// </summary>
/// <remarks>
///     <para>
///         Resolved from the <em>application</em> service provider, so an implementation can depend
///         on whatever resolves the tenant for the rest of the request.
///     </para>
///     <para>
///         Most multi-tenant models do not need this. <c>MultiTenant(t =&gt;
///         t.FromEntityProperty("TenantId"))</c> reads the tenant off the audited entity, including
///         from a shadow property — which is exactly what Finbuckle.MultiTenant's
///         <c>IsMultiTenant()</c> adds — so the common case needs no provider and no reference to
///         any multi-tenancy library.
///     </para>
/// </remarks>
public interface IAuditTenantProvider
{
    /// <summary>Gets the tenant the change currently being saved belongs to.</summary>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    ValueTask<string?> GetTenantIdAsync(CancellationToken cancellationToken);
}
