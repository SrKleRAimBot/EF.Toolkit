using System.Globalization;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Carries <see cref="BulkOptions" /> through <c>DbContextOptions</c> and registers them with
///     EF Core's internal service provider.
/// </summary>
/// <remarks>
///     This extension deliberately does <em>not</em> swap any EF service. The
///     <see cref="Microsoft.EntityFrameworkCore.Update.IModificationCommandBatchFactory" />
///     replacement is applied by the provider package through
///     <c>DbContextOptionsBuilder.ReplaceService</c>, because the replacement type derives from the
///     provider's own factory so that unsupported partitions can fall back to a genuine
///     provider-specific batch.
/// </remarks>
public sealed class BulkOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Initializes a new instance carrying <see cref="BulkOptions.Default" />.</summary>
    public BulkOptionsExtension()
        : this(BulkOptions.Default)
    {
    }

    /// <summary>Initializes a new instance carrying <paramref name="options" />.</summary>
    /// <param name="options">The settings to carry.</param>
    /// <param name="executorType">
    ///     The provider's <see cref="Execution.IBulkOperationExecutor" /> implementation, or
    ///     <see langword="null" /> to register none — in which case every partition falls back to
    ///     stock EF Core.
    /// </param>
    /// <param name="supportingServices">
    ///     Additional provider types the executor depends on, registered as singletons.
    /// </param>
    public BulkOptionsExtension(
        BulkOptions options,
        Type? executorType = null,
        IReadOnlyList<Type>? supportingServices = null)
    {
        Options = options;
        ExecutorType = executorType;
        SupportingServices = supportingServices ?? [];
    }

    /// <summary>Provider types the executor depends on, registered as singletons.</summary>
    public IReadOnlyList<Type> SupportingServices { get; }

    /// <summary>The EF.Toolkit.Bulk settings configured for this context.</summary>
    public BulkOptions Options { get; }

    /// <summary>The provider's bulk executor implementation, if any.</summary>
    public Type? ExecutorType { get; }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        services.AddSingleton(Options);

        if (ExecutorType is not null)
        {
            // Scoped, matching the lifetime of the provider services (connection, SQL generation
            // helper) an executor depends on.
            services.AddScoped(typeof(IBulkOperationExecutor), ExecutorType);

            foreach (var supporting in SupportingServices)
            {
                // Singleton: these hold schema facts (which sequence backs which column) that are
                // fixed for the lifetime of the process, so the cache should outlive a request.
                services.AddSingleton(supporting);
            }
        }
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(BulkOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new BulkOptionsExtension Extension => (BulkOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
        {
            get
            {
                var o = Extension.Options;
                return $"using EF.Toolkit.Bulk (threshold={o.Threshold}, maxBatchSize={o.MaxBatchSize}, "
                    + $"mergeCounts={o.MergeCounts}, onUnsupported={o.OnUnsupported}) ";
            }
        }

        // EF caches one internal service provider per distinct hash, and BulkOptions is registered
        // as a singleton there, so every setting must contribute or two differently-configured
        // contexts would share the first one's options.
        public override int GetServiceProviderHashCode()
            => HashCode.Combine(Extension.Options, Extension.ExecutorType);

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o
                && Extension.Options == o.Extension.Options
                && Extension.ExecutorType == o.Extension.ExecutorType;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            var o = Extension.Options;
            debugInfo["EF.Toolkit.Bulk:Threshold"] = o.Threshold.ToString(CultureInfo.InvariantCulture);
            debugInfo["EF.Toolkit.Bulk:MaxBatchSize"] = o.MaxBatchSize.ToString(CultureInfo.InvariantCulture);
            debugInfo["EF.Toolkit.Bulk:MergeCounts"] = o.MergeCounts.ToString();
            debugInfo["EF.Toolkit.Bulk:OnUnsupported"] = o.OnUnsupported.ToString();
            debugInfo["EF.Toolkit.Bulk:Timeout"] = o.Timeout?.ToString() ?? "(context default)";
        }
    }
}
