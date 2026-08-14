using System.Reflection;
using System.Text.Json;
using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Tests.Configuration;

public class AuditOptionsExtensionTests
{
    [Fact]
    public void Is_not_a_database_provider()
        => new AuditOptionsExtension().Info.IsDatabaseProvider.ShouldBeFalse();

    [Fact]
    public void Identical_settings_share_a_service_provider()
    {
        var left = new AuditOptionsExtension(AuditOptions.Default with { Schema = "trail" });
        var right = new AuditOptionsExtension(AuditOptions.Default with { Schema = "trail" });

        left.Info.GetServiceProviderHashCode().ShouldBe(right.Info.GetServiceProviderHashCode());
        left.Info.ShouldUseSameServiceProvider(right.Info).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Schema", "elsewhere")]
    [InlineData("TableName", "Trail")]
    [InlineData("SharedAuditTables", true)]
    [InlineData("AuditAllEntities", true)]
    [InlineData("MaxValueLength", 17)]
    [InlineData("MaskToken", "[redacted]")]
    [InlineData("BatchThreshold", 7)]
    [InlineData("CaptureBeforeImages", false)]
    [InlineData("RequireActor", true)]
    [InlineData("TenantPropertyName", "OrgId")]
    [InlineData("RequireTenant", true)]
    [InlineData("StoreGeneratedIds", true)]
    public void Every_setting_changes_the_service_provider(string property, object value)
    {
        // EF caches one internal service provider per hash and registers AuditOptions as a
        // singleton there, so a setting that does not contribute would let two differently
        // configured contexts silently share the first one's configuration.
        var changed = With(property, value);

        new AuditOptionsExtension(changed).Info.GetServiceProviderHashCode()
            .ShouldNotBe(new AuditOptionsExtension(AuditOptions.Default).Info.GetServiceProviderHashCode());
    }

    [Fact]
    public void Every_settable_property_is_covered_by_the_hash()
    {
        // A guard on the guard: the theory above enumerates settings by hand, and this fails when
        // one is added that it does not name and that no other test would notice.
        var covered = new HashSet<string>(StringComparer.Ordinal)
        {
            // Scalars, exercised by the theory.
            "Schema", "TableName", "SharedAuditTables", "AuditAllEntities", "MaxValueLength",
            "MaskToken", "BatchThreshold", "CaptureBeforeImages", "RequireActor",
            "TenantPropertyName", "RequireTenant", "StoreGeneratedIds",

            // Enums and reference types, covered below.
            "Operations", "PayloadNames", "StoreEntityTypeAs", "Atomicity", "OnAuditFailure",
            "Indexes", "KeyType", "IdFactory", "IdProviderType", "MaskPredicate",
            "ActorResolver", "TenantResolver", "SinkType", "ExternalContextType", "Json",
            "TimeProvider", "StoreTypes",

            // Derived, not settable.
            "IsMultiTenant",
        };

        var actual = typeof(AuditOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n != "EqualityContract")
            .ToList();

        actual.Except(covered).ShouldBeEmpty(
            "a new AuditOptions property is not accounted for in the service-provider hash tests");
    }

    [Fact]
    public void Reference_typed_settings_change_the_service_provider_too()
    {
        var baseline = new AuditOptionsExtension(AuditOptions.Default).Info.GetServiceProviderHashCode();

        Hash(AuditOptions.Default with { Operations = AuditOperations.Insert }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { PayloadNames = AuditPayloadNames.Column }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { StoreEntityTypeAs = AuditEntityTypeNames.FullName }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { Atomicity = AuditAtomicity.BestEffort }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { OnAuditFailure = AuditFailure.Ignore }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { Indexes = AuditIndexes.None }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { KeyType = typeof(long) }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { IdFactory = (Func<IServiceProvider, Guid>)(_ => Guid.Empty) })
            .ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { IdProviderType = typeof(string) }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { MaskPredicate = static (IProperty _) => true }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { ActorResolver = static (_, _) => ValueTask.FromResult(default(AuditActor)) })
            .ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { TenantResolver = static (_, _) => ValueTask.FromResult<string?>(null) })
            .ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { SinkType = typeof(string) }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { ExternalContextType = typeof(string) }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { Json = new JsonSerializerOptions() }).ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { TimeProvider = new Infrastructure.FixedTimeProvider(default) })
            .ShouldNotBe(baseline);
        Hash(AuditOptions.Default with { StoreTypes = new AuditStoreTypes { Json = "jsonb" } })
            .ShouldNotBe(baseline);
    }

    [Fact]
    public void Debug_info_names_every_setting_it_reports()
    {
        var debug = new Dictionary<string, string>();
        new AuditOptionsExtension().Info.PopulateDebugInfo(debug);

        debug.Keys.ShouldAllBe(k => k.StartsWith("EF.Toolkit.Audit:", StringComparison.Ordinal));
        debug["EF.Toolkit.Audit:Schema"].ShouldBe("audit");
    }

    private static int Hash(AuditOptions options)
        => new AuditOptionsExtension(options).Info.GetServiceProviderHashCode();

    private static AuditOptions With(string property, object value)
    {
        // AuditOptions is a record with init-only properties, so a copy is made through the
        // compiler-generated clone and the one property is set on it.
        var clone = (AuditOptions)typeof(AuditOptions)
            .GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(AuditOptions.Default, null)!;

        typeof(AuditOptions).GetProperty(property)!
            .GetSetMethod(nonPublic: true)!
            .Invoke(clone, [value]);

        return clone;
    }
}
