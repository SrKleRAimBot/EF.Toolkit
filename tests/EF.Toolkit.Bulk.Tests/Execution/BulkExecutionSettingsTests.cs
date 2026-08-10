using EFToolkit.Bulk.Execution;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     Timeout resolution. Before this existed, the raw commands in the staging paths ran at
///     ADO.NET's 30-second default however the context was configured, so a staged statement over a
///     large table failed for a reason nothing in the configuration accounted for.
/// </summary>
public class BulkExecutionSettingsTests
{
    [Fact]
    public void Per_call_timeout_wins_over_everything()
    {
        var settings = BulkExecutionSettings.Resolve(
            perCall: TimeSpan.FromMinutes(2),
            contextWide: TimeSpan.FromMinutes(5),
            commandTimeoutSeconds: 90);

        settings.Timeout.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Context_wide_timeout_wins_over_efs_command_timeout()
    {
        var settings = BulkExecutionSettings.Resolve(
            perCall: null,
            contextWide: TimeSpan.FromMinutes(5),
            commandTimeoutSeconds: 90);

        settings.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    // The case that was broken: nothing bulk-specific configured, but the context has a command
    // timeout, and the staged statements ignored it entirely.
    [Fact]
    public void Efs_command_timeout_applies_when_nothing_bulk_specific_is_set()
    {
        var settings = BulkExecutionSettings.Resolve(
            perCall: null, contextWide: null, commandTimeoutSeconds: 90);

        settings.Timeout.ShouldBe(TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void No_timeout_anywhere_leaves_the_providers_default_in_place()
    {
        var settings = BulkExecutionSettings.Resolve(
            perCall: null, contextWide: null, commandTimeoutSeconds: null);

        settings.Timeout.ShouldBeNull();
        settings.Seconds(fallback: 30).ShouldBe(30);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(45, 45)]
    [InlineData(300, 300)]
    public void Whole_seconds_pass_through_unchanged(int seconds, int expected)
        => BulkExecutionSettings.Resolve(TimeSpan.FromSeconds(seconds), null, null)
            .Seconds(fallback: 30)
            .ShouldBe(expected);

    // Every provider reads zero as "no limit", so rounding a sub-second timeout down would mean
    // asking for 200ms and getting no deadline at all.
    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(999)]
    public void Sub_second_timeouts_round_up_rather_than_to_no_limit(int milliseconds)
        => BulkExecutionSettings.Resolve(TimeSpan.FromMilliseconds(milliseconds), null, null)
            .Seconds(fallback: 30)
            .ShouldBe(1);

    [Fact]
    public void Fractional_seconds_round_up_so_the_deadline_is_never_shortened()
        => BulkExecutionSettings.Resolve(TimeSpan.FromSeconds(2.4), null, null)
            .Seconds(fallback: 30)
            .ShouldBe(3);
}
