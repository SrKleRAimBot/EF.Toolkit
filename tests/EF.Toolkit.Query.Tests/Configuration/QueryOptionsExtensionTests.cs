using EFToolkit.Query.Configuration;

namespace EFToolkit.Query.Tests.Configuration;

/// <summary>
///     Covers the service-provider hash, which is the setting that silently corrupts every other one
///     when it is wrong.
/// </summary>
/// <remarks>
///     EF caches one internal service provider per distinct extension hash, and
///     <see cref="QueryOptions" /> is registered as a singleton inside it. A setting left out of the
///     hash means two contexts configured differently share the first one's options — so a page-size
///     ceiling set for one tenant would quietly apply to another.
/// </remarks>
public class QueryOptionsExtensionTests
{
    [Theory]
    [InlineData("DefaultPageSize")]
    [InlineData("MaxPageSize")]
    [InlineData("Numbering")]
    [InlineData("CountStrategy")]
    [InlineData("MaxOffsetRows")]
    [InlineData("BatchSize")]
    [InlineData("MaxInClauseValues")]
    [InlineData("TrackingScopes")]
    [InlineData("Diagnostics.Checks")]
    [InlineData("Diagnostics.Behavior")]
    public void Changing_any_single_setting_changes_the_service_provider_hash(string setting)
    {
        var baseline = new QueryOptionsExtension(QueryOptions.Default).Info;
        var other = new QueryOptionsExtension(Change(setting)).Info;

        other.GetServiceProviderHashCode()
            .ShouldNotBe(baseline.GetServiceProviderHashCode(), $"{setting} must contribute");
        other.ShouldUseSameServiceProvider(baseline).ShouldBeFalse($"{setting} must contribute");
    }

    [Fact]
    public void Identical_settings_share_a_service_provider()
    {
        var left = new QueryOptionsExtension(QueryOptions.Default with { MaxPageSize = 42 }).Info;
        var right = new QueryOptionsExtension(QueryOptions.Default with { MaxPageSize = 42 }).Info;

        left.GetServiceProviderHashCode().ShouldBe(right.GetServiceProviderHashCode());
        left.ShouldUseSameServiceProvider(right).ShouldBeTrue();
    }

    [Fact]
    public void The_extension_is_not_a_database_provider()
        => new QueryOptionsExtension().Info.IsDatabaseProvider.ShouldBeFalse();

    [Fact]
    public void Debug_info_names_every_setting_under_the_package_prefix()
    {
        var debugInfo = new Dictionary<string, string>();
        new QueryOptionsExtension().Info.PopulateDebugInfo(debugInfo);

        debugInfo.Keys.ShouldAllBe(k => k.StartsWith("EF.Toolkit.Query:", StringComparison.Ordinal));
        debugInfo.ShouldContainKey("EF.Toolkit.Query:DefaultPageSize");
        debugInfo.ShouldContainKey("EF.Toolkit.Query:TrackingScopes");
        debugInfo.ShouldContainKey("EF.Toolkit.Query:OnWarning");
    }

    [Fact]
    public void The_log_fragment_names_the_package()
        => new QueryOptionsExtension().Info.LogFragment.ShouldContain("EF.Toolkit.Query");

    [Fact]
    public void The_extension_rejects_null_options()
        => Should.Throw<ArgumentNullException>(() => new QueryOptionsExtension(null!));

    private static QueryOptions Change(string setting) => setting switch
    {
        "DefaultPageSize" => QueryOptions.Default with { DefaultPageSize = 7 },
        "MaxPageSize" => QueryOptions.Default with { MaxPageSize = 7 },
        "Numbering" => QueryOptions.Default with { Numbering = PageNumbering.ZeroBased },
        "CountStrategy" => QueryOptions.Default with { CountStrategy = PageCountStrategy.None },
        "MaxOffsetRows" => QueryOptions.Default with { MaxOffsetRows = 7 },
        "BatchSize" => QueryOptions.Default with { BatchSize = 7 },
        "MaxInClauseValues" => QueryOptions.Default with { MaxInClauseValues = 7 },
        "TrackingScopes" => QueryOptions.Default with { TrackingScopes = false },
        "Diagnostics.Checks" => QueryOptions.Default with
        {
            Diagnostics = QueryDiagnosticsOptions.Default with { Checks = QueryChecks.DeepOffset },
        },
        "Diagnostics.Behavior" => QueryOptions.Default with
        {
            Diagnostics = QueryDiagnosticsOptions.Default with
            {
                Behavior = QueryWarningBehavior.Throw,
            },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Unknown setting."),
    };
}
