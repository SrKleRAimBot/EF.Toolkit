using System.Globalization;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Capture;
using EFToolkit.Audit.Infrastructure;
using EFToolkit.Audit.Sinks;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Carries <see cref="AuditOptions" /> through <c>DbContextOptions</c> and registers the capture
///     pipeline with EF Core's internal service provider.
/// </summary>
/// <remarks>
///     The <see cref="Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer" /> replacement
///     that adds the audit entity type is applied by the provider package through
///     <c>DbContextOptionsBuilder.ReplaceService</c>, because it needs the provider's store types to
///     do it — the same division of labour EF.Toolkit.Bulk uses for its batch factory.
/// </remarks>
public sealed class AuditOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Initializes a new instance carrying <see cref="AuditOptions.Default" />.</summary>
    public AuditOptionsExtension()
        : this(AuditOptions.Default)
    {
    }

    /// <summary>Initializes a new instance carrying <paramref name="options" />.</summary>
    /// <param name="options">The settings to carry.</param>
    public AuditOptionsExtension(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The auditing settings configured for this context.</summary>
    public AuditOptions Options { get; }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(Options);

        // Scoped, because EF builds one internal service-provider scope per DbContext instance.
        // That is what makes it safe for the interceptor to hold the change set it captured in
        // SavingChanges until SavedChanges: the instance belongs to exactly one context, and a
        // DbContext does not perform two saves at once.
        services.AddScoped(
            typeof(IAuditEntryFactory),
            typeof(AuditEntryFactory<>).MakeGenericType(Options.KeyType));

        services.AddScoped(typeof(IAuditSink), SinkType());
        services.AddScoped<IInterceptor, AuditSaveChangesInterceptor>();

        // How [Audited] and friends reach the model without the application calling anything in
        // OnModelCreating.
        services.AddScoped<IConventionSetPlugin, AuditConventionSetPlugin>();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Checked here rather than in the builder because these are relationships between settings,
    ///     and checking them as each one is set would make the outcome depend on the order the calls
    ///     happened to be written in.
    /// </remarks>
    public void Validate(IDbContextOptions options)
    {
        // Only the external-context sink is refused, and only because it demonstrably cannot be
        // atomic: it has its own connection and its own transaction. A custom IAuditSink is handed
        // the change's transaction on AuditWriteContext and may perfectly well write inside it, so
        // refusing that outright would rule out the legitimate case — a sink that writes to another
        // table in the same database — for the sake of the illegitimate one.
        if (Options.Atomicity == AuditAtomicity.SameTransaction
            && Options.ExternalContextType is not null)
        {
            throw new AuditNotSupportedException(
                $"Audit entries are configured to be written through '{Options.ExternalContextType.Name}', "
                + "which has its own connection and so its own transaction, while Atomicity is "
                + "SameTransaction. That cannot be honoured. Add "
                + "Atomicity(AuditAtomicity.BestEffort) to say the guarantee is being given up, or "
                + "write to the same context.");
        }

        if (Options.RequireTenant && !Options.IsMultiTenant)
        {
            throw new AuditNotSupportedException(
                "MultiTenant(t => t.Require()) refuses an audit entry with no tenant, but no "
                + "tenant source was configured, so no entry could ever have one. Add "
                + "FromEntityProperty(...) or FromProvider<...>().");
        }

        if (Options.StoreGeneratedIds && Options.KeyType == typeof(Guid))
        {
            throw new AuditNotSupportedException(
                "Store-generated Guid audit entry keys are refused. A database-generated Guid is "
                + "neither ordered nor free to read back, so it is worse than the client-generated "
                + "UUIDv7 default on both counts. Use BigIntKeys(), or Ids<Guid>(...) with a "
                + "generator of your own.");
        }
    }

    private Type SinkType()
    {
        if (Options.SinkType is not null)
        {
            return typeof(ResolvedAuditSink);
        }

        return Options.ExternalContextType is not null
            ? typeof(ExternalContextAuditSink<>).MakeGenericType(Options.KeyType)
            : typeof(SameContextAuditSink<>).MakeGenericType(Options.KeyType);
    }

    private sealed class ExtensionInfo(AuditOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new AuditOptionsExtension Extension => (AuditOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
        {
            get
            {
                var o = Extension.Options;
                return $"using EF.Toolkit.Audit (schema={o.Schema ?? "(default)"}, "
                    + $"table={o.TableName}, keys={o.KeyType.Name}, atomicity={o.Atomicity}) ";
            }
        }

        // EF caches one internal service provider per distinct hash, and AuditOptions is registered
        // as a singleton there, so every setting must contribute or two differently-configured
        // contexts would share the first one's options. AuditOptions is a record precisely so that
        // holds without anyone having to remember to extend this.
        public override int GetServiceProviderHashCode()
            => Extension.Options.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && Extension.Options == o.Extension.Options;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            var o = Extension.Options;
            debugInfo["EF.Toolkit.Audit:Schema"] = o.Schema ?? "(default)";
            debugInfo["EF.Toolkit.Audit:TableName"] = o.TableName;
            debugInfo["EF.Toolkit.Audit:SharedAuditTables"] = o.SharedAuditTables.ToString();
            debugInfo["EF.Toolkit.Audit:AuditAllEntities"] = o.AuditAllEntities.ToString();
            debugInfo["EF.Toolkit.Audit:Operations"] = o.Operations.ToString();
            debugInfo["EF.Toolkit.Audit:KeyType"] = o.KeyType.FullName ?? o.KeyType.Name;
            debugInfo["EF.Toolkit.Audit:Atomicity"] = o.Atomicity.ToString();
            debugInfo["EF.Toolkit.Audit:OnAuditFailure"] = o.OnAuditFailure.ToString();
            debugInfo["EF.Toolkit.Audit:Indexes"] = o.Indexes.ToString();
            debugInfo["EF.Toolkit.Audit:MaxValueLength"] =
                o.MaxValueLength.ToString(CultureInfo.InvariantCulture);
            debugInfo["EF.Toolkit.Audit:BatchThreshold"] =
                o.BatchThreshold.ToString(CultureInfo.InvariantCulture);
        }
    }
}
