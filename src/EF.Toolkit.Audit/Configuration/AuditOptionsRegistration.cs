using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Shared plumbing behind each provider package's <c>UseAuditing()</c>.
/// </summary>
/// <remarks>
///     The entry point lives in the provider package rather than here so that provider-only
///     settings — the payload's store type, the index method that makes it searchable — surface
///     only where they mean something, and so that no reflection-based provider discovery is
///     needed.
/// </remarks>
public static class AuditOptionsRegistration
{
    /// <summary>
    ///     Builds an <see cref="AuditOptions" /> from <paramref name="configure" /> and attaches it
    ///     to <paramref name="optionsBuilder" />.
    /// </summary>
    /// <typeparam name="TBuilder">The provider's options-builder type.</typeparam>
    /// <param name="optionsBuilder">The context options builder being configured.</param>
    /// <param name="createBuilder">
    ///     Creates the provider's options builder, seeded with that provider's store types.
    /// </param>
    /// <param name="configure">Optional user configuration.</param>
    /// <returns>The settings that were attached.</returns>
    public static AuditOptions Apply<TBuilder>(
        DbContextOptionsBuilder optionsBuilder,
        Func<AuditOptions, TBuilder> createBuilder,
        Action<TBuilder>? configure)
        where TBuilder : AuditOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(createBuilder);

        var builder = createBuilder(AuditOptions.Default);
        configure?.Invoke(builder);

        var extension = new AuditOptionsExtension(builder.Options);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);

        return builder.Options;
    }
}
