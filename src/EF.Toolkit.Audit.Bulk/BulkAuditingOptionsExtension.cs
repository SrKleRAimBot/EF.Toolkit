using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using EFToolkit.Bulk.Configuration;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFToolkit.Audit.Bulk;

/// <summary>
///     Registers the two services that join auditing to the explicit bulk API.
/// </summary>
/// <remarks>
///     Carries no settings of its own. Everything it needs is already configured on one side or the
///     other, and adding a third place to configure the same things is how two packages that were
///     meant to be independent stop being so.
/// </remarks>
public sealed class BulkAuditingOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped, matching the audit factory and sink it depends on, which EF scopes per DbContext.
        services.AddScoped<IBulkWriteObserver, AuditBulkWriteObserver>();
        services.AddSingleton<IAuditBatchWriter, BulkAuditBatchWriter>();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Both halves are checked, because this package does nothing on its own and a missing call
    ///     would otherwise show up as an audit trail that is silently short of every bulk operation
    ///     the application performs — the hardest kind of gap to notice.
    /// </remarks>
    public void Validate(IDbContextOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.FindExtension<AuditOptionsExtension>() is null)
        {
            throw new AuditNotSupportedException(
                "UseBulkAuditing() was called without UseAuditing(). It joins the two packages "
                + "together and has nothing to join. Add UseAuditing(...) after the provider.");
        }

        if (options.FindExtension<BulkOptionsExtension>() is null)
        {
            throw new AuditNotSupportedException(
                "UseBulkAuditing() was called without UseBulkOperations(). It exists to audit the "
                + "explicit bulk API, which is not registered. Add UseBulkOperations() after the "
                + "provider, or drop UseBulkAuditing() — auditing of SaveChanges works without it.");
        }
    }

    private sealed class ExtensionInfo(BulkAuditingOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using EF.Toolkit.Audit.Bulk ";

        public override int GetServiceProviderHashCode() => typeof(BulkAuditingOptionsExtension).GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other)
            => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["EF.Toolkit.Audit.Bulk:Enabled"] = "True";
    }
}
