using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Shared plumbing behind each provider package's <c>UseBulkOperations()</c>.
/// </summary>
/// <remarks>
///     The entry point itself lives in the provider package rather than here, for two reasons: it
///     avoids reflection-based provider discovery, and the
///     <see cref="Microsoft.EntityFrameworkCore.Update.IModificationCommandBatchFactory" />
///     replacement must derive from the provider's own factory so that unsupported partitions can
///     fall back to a genuine provider batch.
/// </remarks>
public static class BulkOptionsRegistration
{
    /// <summary>
    ///     Builds a <see cref="BulkOptions" /> from <paramref name="configure" /> and attaches it to
    ///     <paramref name="optionsBuilder" />.
    /// </summary>
    /// <typeparam name="TBuilder">The provider's options-builder type.</typeparam>
    /// <param name="optionsBuilder">The context options builder being configured.</param>
    /// <param name="createBuilder">
    ///     Creates the provider's options builder, so provider-specific knobs surface in
    ///     <paramref name="configure" />.
    /// </param>
    /// <param name="configure">Optional user configuration.</param>
    /// <param name="executorType">
    ///     The provider's <see cref="Execution.IBulkOperationExecutor" /> implementation.
    /// </param>
    /// <param name="supportingServices">
    ///     Additional provider types the executor depends on, registered as singletons.
    /// </param>
    /// <returns>The settings that were attached.</returns>
    public static BulkOptions Apply<TBuilder>(
        DbContextOptionsBuilder optionsBuilder,
        Func<BulkOptions, TBuilder> createBuilder,
        Action<TBuilder>? configure,
        Type? executorType = null,
        IReadOnlyList<Type>? supportingServices = null)
        where TBuilder : BulkOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(createBuilder);

        var builder = createBuilder(BulkOptions.Default);
        configure?.Invoke(builder);

        var extension = new BulkOptionsExtension(builder.Options, executorType, supportingServices);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return builder.Options;
    }
}
