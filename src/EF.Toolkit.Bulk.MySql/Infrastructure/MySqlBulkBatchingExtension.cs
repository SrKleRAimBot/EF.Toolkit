using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Bulk.MySql.Infrastructure;

/// <summary>
///     Decorates the MySQL provider's <see cref="IModificationCommandBatchFactory" /> registration
///     with <see cref="MySqlBulkModificationCommandBatchFactory" />.
/// </summary>
/// <remarks>
///     <para>
///         <c>ReplaceService</c> cannot do this: it discards the registration it replaces, and the
///         replaced factory is the one every fallback needs. Instead this extension runs after the
///         provider's own — EF applies extensions in the order they were added, which is why
///         <c>UseBulkOperations()</c> must follow the provider call — finds the factory the provider
///         registered, and re-registers it behind the decorator.
///     </para>
///     <para>
///         The provider's factory is still built by the container, from its own constructor, with
///         its own dependencies and lifetime. Nothing about it is assumed beyond the interface.
///     </para>
/// </remarks>
public sealed class MySqlBulkBatchingExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var provider = services.LastOrDefault(
                d => d.ServiceType == typeof(IModificationCommandBatchFactory))
            ?? throw new InvalidOperationException(
                "EF.Toolkit.Bulk.MySql found no batch factory to extend. Call UseMySQL() or "
                + "UseMySql() before UseBulkOperations(), so the MySQL provider's services are "
                + "registered first.");

        if (provider.ImplementationType == typeof(MySqlBulkModificationCommandBatchFactory))
        {
            return;
        }

        services.Remove(provider);
        services.Add(
            new ServiceDescriptor(
                typeof(IModificationCommandBatchFactory),
                sp => new MySqlBulkModificationCommandBatchFactory(
                    Create(sp, provider),
                    sp.GetRequiredService<BulkOptions>(),
                    sp.GetRequiredService<IBulkOperationExecutor>()),
                provider.Lifetime));
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
    }

    private static IModificationCommandBatchFactory Create(
        IServiceProvider services,
        ServiceDescriptor descriptor)
        => (IModificationCommandBatchFactory)(
            descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(services)
            ?? ActivatorUtilities.CreateInstance(services, descriptor.ImplementationType!));

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "";

        // Carries no settings, so every instance is interchangeable.
        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
        }
    }
}
