using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Audit.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Turns described changes into audit entries. The one place that decides what an entry is.
/// </summary>
/// <typeparam name="TKey">The configured entry key type.</typeparam>
/// <remarks>
///     Every capture path goes through here — the change tracker, the explicit bulk API, anything
///     an application writes itself — which is what makes a bulk-written row's trail
///     indistinguishable from a <c>SaveChanges</c>-written one. Registration, exclusions, masking,
///     key formatting, the payload shape, the actor, the tenant and the clock are all resolved in
///     this class and nowhere else.
/// </remarks>
internal sealed class AuditEntryFactory<TKey> : IAuditEntryFactory
{
    private readonly AuditOptions _options;
    private readonly IServiceProvider? _applicationServices;

    private IAuditEntryIdProvider<TKey>? _ids;

    public AuditEntryFactory(AuditOptions options, IDbContextOptions contextOptions)
    {
        _options = options;
        _applicationServices = contextOptions
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;
    }

    /// <inheritdoc />
    public bool Audits(IEntityType entityType, AuditOperation operation)
        => AuditEntityPlan.For(entityType, _options).Audits(operation);

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AuditEntry>> CreateAsync(
        IReadOnlyList<IAuditCaptureSource> sources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            return [];
        }

        var scope = AuditScope.Current;
        var actor = await ResolveActorAsync(scope, cancellationToken).ConfigureAwait(false);
        var fallbackTenant = await ResolveTenantAsync(scope, cancellationToken).ConfigureAwait(false);
        var occurredAt = _options.TimeProvider.GetUtcNow();
        var metadata = scope?.Metadata ?? EmptyMetadata;

        var entries = new List<AuditEntry<TKey>>();

        using (var payloads = new AuditPayloadWriter(_options))
        {
            foreach (var source in sources)
            {
                Capture(source, payloads, actor, fallbackTenant, occurredAt, scope, metadata, entries);
            }
        }

        AssignIds(entries);

        AuditDiagnostics.ReportEntriesCaptured(sources[0].Source, sources.Count, entries.Count);

        return entries;
    }

    private static IReadOnlyDictionary<string, object?> EmptyMetadata { get; }
        = new Dictionary<string, object?>();

    private void Capture(
        IAuditCaptureSource source,
        AuditPayloadWriter payloads,
        AuditActor actor,
        string? fallbackTenant,
        DateTimeOffset occurredAt,
        AuditScope? scope,
        IReadOnlyDictionary<string, object?> metadata,
        List<AuditEntry<TKey>> entries)
    {
        var plan = AuditEntityPlan.For(source.EntityType, _options);

        if (!plan.Audits(source.Operation))
        {
            return;
        }

        var projection = AuditSourceProjection.Create(source, plan);
        var unchanged = 0;

        // The tenant may be a column the source carries rather than something it knows to hand over
        // — a bulk operation reads values, not meaning. Finding it here keeps every capture path on
        // the same footing without any of them having to know how tenancy is configured.
        var tenantIndex = plan.TenantProperty is { } tenantProperty
            ? IndexOf(source.Properties, tenantProperty)
            : -1;

        for (var row = 0; row < source.RowCount; row++)
        {
            var payload = payloads.Write(
                source.Operation, projection, source, row, metadata, scope?.Reason);

            if (payload is null)
            {
                unchanged++;
                continue;
            }

            var tenant = source.GetTenantId(row)
                ?? TenantFrom(source, row, tenantIndex)
                ?? scope?.TenantId
                ?? fallbackTenant;

            if (tenant is null && _options.RequireTenant)
            {
                throw new AuditNotSupportedException(
                    $"An audit entry for '{source.EntityType.DisplayName()}' has no tenant, and "
                    + "MultiTenant(t => t.Require()) is configured. A tenant-less entry is "
                    + "invisible to every tenant-scoped query that will later look for it.");
            }

            entries.Add(new AuditEntry<TKey>
            {
                EntityType = plan.EntityTypeName,
                EntityKey = EntityKey(projection, source, row),
                Operation = source.Operation,
                ActorId = actor.Id,
                ActorName = actor.Name,
                ActorType = actor.Type,
                TenantId = tenant,
                OccurredAt = occurredAt,
                CorrelationId = scope?.CorrelationId,
                Source = source.Source,
                Changes = payload,
            });
        }

        if (unchanged > 0)
        {
            AuditDiagnostics.ReportAuditSkipped(
                source.EntityType.ClrType,
                source.Operation,
                $"{unchanged} of {source.RowCount} rows had no property whose value actually "
                + "changed.");
        }
    }

    private static int IndexOf(IReadOnlyList<IProperty> properties, IProperty property)
    {
        for (var i = 0; i < properties.Count; i++)
        {
            if (properties[i] == property)
            {
                return i;
            }
        }

        return -1;
    }

    private static string? TenantFrom(IAuditCaptureSource source, int row, int index)
    {
        if (index < 0)
        {
            return null;
        }

        var text = AuditValues.ToKeyText(
            AuditValues.ToProvider(source.Properties[index], source.GetCurrentValue(row, index)));

        return text.Length == 0 ? null : text;
    }

    private static string EntityKey(
        AuditSourceProjection projection,
        IAuditCaptureSource source,
        int row)
    {
        var components = new object?[projection.EntityKey.Count];

        for (var i = 0; i < components.Length; i++)
        {
            var component = projection.EntityKey[i];
            components[i] = AuditValues.ToProvider(component.Property, component.Read(source, row));
        }

        return AuditKeyFormatter.Format(components);
    }

    private async ValueTask<AuditActor> ResolveActorAsync(
        AuditScope? scope,
        CancellationToken cancellationToken)
    {
        // An ambient scope wins. It is how a background job says who it is acting as, and how a
        // request-scoped default gets overridden for one operation without reconfiguring anything.
        var actor = scope?.Actor;

        if (actor is null && _options.ActorResolver is { } resolver)
        {
            actor = await resolver(_applicationServices!, cancellationToken).ConfigureAwait(false);
        }

        if (_options.RequireActor && actor is not { IsUnknown: false })
        {
            throw new AuditNotSupportedException(
                "RequireActor() is configured and no actor could be determined for this change. "
                + "Configure ActorFrom(...), or wrap the operation in AuditScope.Begin(...).");
        }

        return actor ?? AuditActor.Unknown;
    }

    private async ValueTask<string?> ResolveTenantAsync(
        AuditScope? scope,
        CancellationToken cancellationToken)
    {
        if (scope?.TenantId is not null || _options.TenantResolver is not { } resolver)
        {
            return null;
        }

        return await resolver(_applicationServices!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Stamps every entry with a key, in one pass.
    /// </summary>
    /// <remarks>
    ///     One call filling a span rather than one call per entry, because a provider that can
    ///     produce a run more cheaply than it can produce one at a time should get the chance —
    ///     an audited bulk operation asks for as many keys as it wrote rows.
    /// </remarks>
    private void AssignIds(List<AuditEntry<TKey>> entries)
    {
        if (entries.Count == 0 || _options.StoreGeneratedIds)
        {
            return;
        }

        var provider = _ids ??= CreateIdProvider();
        var ids = new TKey[entries.Count];
        provider.Generate(ids);

        for (var i = 0; i < entries.Count; i++)
        {
            entries[i].Id = ids[i];
        }
    }

    private IAuditEntryIdProvider<TKey> CreateIdProvider()
    {
        if (_options.IdProviderType is { } providerType)
        {
            if (_applicationServices?.GetService(providerType) is IAuditEntryIdProvider<TKey> resolved)
            {
                return resolved;
            }

            throw new AuditNotSupportedException(
                $"IdsFrom<{providerType.Name}, {typeof(TKey).Name}>() needs '{providerType.Name}' "
                + "from the application's service provider, where it is not registered.");
        }

        if (_options.IdFactory is Func<IServiceProvider, TKey> factory)
        {
            return new DelegateIdProvider(_applicationServices, factory);
        }

        throw new AuditNotSupportedException(
            $"No audit entry key source is configured for '{typeof(TKey).Name}'. Use Ids<{typeof(TKey).Name}>(...), "
            + "IdsFrom<...>(), or BigIntKeys().");
    }

    private sealed class DelegateIdProvider(IServiceProvider? services, Func<IServiceProvider, TKey> factory)
        : IAuditEntryIdProvider<TKey>
    {
        public TKey Generate()
            => factory(services ?? EmptyServiceProvider.Instance);
    }

    /// <summary>
    ///     Stands in where a context was configured without application services.
    /// </summary>
    /// <remarks>
    ///     The default key factory ignores its argument entirely, so a context built from a bare
    ///     <c>DbContextOptionsBuilder</c> — every unit test that does not go through
    ///     <c>AddDbContext</c> — should not fail merely for not having a container. One that does
    ///     use the argument gets a clear "not registered" from here instead of a null reference.
    /// </remarks>
    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}
