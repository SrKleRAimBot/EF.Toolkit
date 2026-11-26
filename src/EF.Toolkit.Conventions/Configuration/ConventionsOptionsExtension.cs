using EFToolkit.Conventions.Saving;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Conventions.Configuration;

/// <summary>The EF Core options extension that carries <see cref="ConventionsOptions" />.</summary>
public sealed class ConventionsOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <summary>Initializes a new instance with the defaults.</summary>
    public ConventionsOptionsExtension()
        : this(ConventionsOptions.Default)
    {
    }

    /// <summary>Initializes a new instance.</summary>
    /// <param name="options">The settings.</param>
    public ConventionsOptionsExtension(ConventionsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The settings.</summary>
    public ConventionsOptions Options { get; }

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(Options);

        // Scoped: EF builds one internal service-provider scope per context instance, so the state
        // (which entities were asked to be hard-deleted, which deletes this save converted) and the
        // interceptor that reads it each belong to exactly one context.
        services.AddScoped<SoftDeleteState>();
        services.AddScoped<IInterceptor, ConventionsSaveChangesInterceptor>();

        // With context pooling the scope outlives a lease: EF resets the context and keeps its services.
        // As a resettable service the state is cleared with it, so a HardDelete() mark from one lease
        // cannot turn a later lease's ordinary Remove() of the same instance into a real delete.
        services.AddScoped<IResettableService>(provider => provider.GetRequiredService<SoftDeleteState>());
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(ConventionsOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new ConventionsOptionsExtension Extension => (ConventionsOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment
            => $"using EF.Toolkit.Conventions (softDelete={Extension.Options.SoftDelete}, "
                + $"timestamps={Extension.Options.Timestamps}) ";

        // Options are a singleton in EF's internal provider, so a context with a different clock must
        // not share a provider with this one. A record's hash covers every setting.
        public override int GetServiceProviderHashCode() => Extension.Options.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo o && Extension.Options == o.Extension.Options;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);
            debugInfo["EF.Toolkit.Conventions:SoftDelete"] = Extension.Options.SoftDelete.ToString();
            debugInfo["EF.Toolkit.Conventions:Timestamps"] = Extension.Options.Timestamps.ToString();
            debugInfo["EF.Toolkit.Conventions:Clock"] = Extension.Options.Clock.GetType().Name;
        }
    }
}
