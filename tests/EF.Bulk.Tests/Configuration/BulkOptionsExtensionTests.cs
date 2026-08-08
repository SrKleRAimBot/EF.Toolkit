using EFBulk.Configuration;
using Shouldly;

namespace EFBulk.Tests.Configuration;

public class BulkOptionsExtensionTests
{
    [Fact]
    public void Default_options_match_documented_defaults()
    {
        var o = BulkOptions.Default;

        o.Threshold.ShouldBe(BulkOptions.DefaultThreshold);
        o.MaxBatchSize.ShouldBe(BulkOptions.DefaultMaxBatchSize);
        o.KeyAllocation.ShouldBe(KeyAllocation.ReserveBlocks);
        o.OnUnsupported.ShouldBe(Unsupported.FallBack);
        o.Timeout.ShouldBeNull();
    }

    [Fact]
    public void Builder_accumulates_settings_without_mutating_the_seed()
    {
        var builder = new BulkOptionsBuilder(BulkOptions.Default);

        builder.Threshold(7)
            .MaxBatchSize(1234)
            .KeyAllocation(KeyAllocation.Staging)
            .OnUnsupported(Unsupported.Throw)
            .Timeout(TimeSpan.FromMinutes(3));

        builder.Options.Threshold.ShouldBe(7);
        builder.Options.MaxBatchSize.ShouldBe(1234);
        builder.Options.KeyAllocation.ShouldBe(KeyAllocation.Staging);
        builder.Options.OnUnsupported.ShouldBe(Unsupported.Throw);
        builder.Options.Timeout.ShouldBe(TimeSpan.FromMinutes(3));

        BulkOptions.Default.Threshold.ShouldBe(BulkOptions.DefaultThreshold);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Builder_rejects_non_positive_sizes(int value)
    {
        var builder = new BulkOptionsBuilder(BulkOptions.Default);

        Should.Throw<ArgumentOutOfRangeException>(() => builder.Threshold(value));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.MaxBatchSize(value));
    }

    [Fact]
    public void Builder_rejects_non_positive_timeout()
    {
        var builder = new BulkOptionsBuilder(BulkOptions.Default);

        Should.Throw<ArgumentOutOfRangeException>(() => builder.Timeout(TimeSpan.Zero));
        Should.Throw<ArgumentOutOfRangeException>(() => builder.Timeout(TimeSpan.FromSeconds(-1)));
    }

    // EF caches one internal service provider per distinct extension hash, and BulkOptions is
    // registered as a singleton in it. If two differently-configured contexts hashed equal, the
    // second would silently run with the first one's settings.
    [Fact]
    public void Differing_options_do_not_share_a_service_provider()
    {
        var a = new BulkOptionsExtension(BulkOptions.Default);
        var b = new BulkOptionsExtension(BulkOptions.Default with { Threshold = 999 });

        a.Info.ShouldUseSameServiceProvider(b.Info).ShouldBeFalse();
        a.Info.GetServiceProviderHashCode().ShouldNotBe(b.Info.GetServiceProviderHashCode());
    }

    [Fact]
    public void Equivalent_options_share_a_service_provider()
    {
        var a = new BulkOptionsExtension(BulkOptions.Default with { MaxBatchSize = 10 });
        var b = new BulkOptionsExtension(BulkOptions.Default with { MaxBatchSize = 10 });

        a.Info.ShouldUseSameServiceProvider(b.Info).ShouldBeTrue();
        a.Info.GetServiceProviderHashCode().ShouldBe(b.Info.GetServiceProviderHashCode());
    }

    [Fact]
    public void Debug_info_reports_every_setting()
    {
        var extension = new BulkOptionsExtension(BulkOptions.Default with
        {
            KeyAllocation = KeyAllocation.Staging
        });

        var debugInfo = new Dictionary<string, string>();
        extension.Info.PopulateDebugInfo(debugInfo);

        debugInfo["EFBulk:KeyAllocation"].ShouldBe(nameof(KeyAllocation.Staging));
        debugInfo.ShouldContainKey("EFBulk:Threshold");
        debugInfo.ShouldContainKey("EFBulk:MaxBatchSize");
        debugInfo.ShouldContainKey("EFBulk:OnUnsupported");
        debugInfo.ShouldContainKey("EFBulk:Timeout");
    }
}
