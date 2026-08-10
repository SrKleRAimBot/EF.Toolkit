using EFToolkit.Bulk.Execution;
using Shouldly;

namespace EFToolkit.Bulk.Tests.Execution;

/// <summary>
///     When a staging table is worth indexing before the statement that joins it.
/// </summary>
public class StagingPreludeTests
{
    [Theory]
    [InlineData(5_000)]
    [InlineData(50_000)]
    public void Indexes_at_and_above_the_threshold(int rows)
        => StagingPrelude.ShouldIndex(rows, joinColumns: 1, threshold: 5_000).ShouldBeTrue();

    [Fact]
    public void Does_not_index_below_the_threshold()
        => StagingPrelude.ShouldIndex(4_999, joinColumns: 1, threshold: 5_000).ShouldBeFalse();

    // The index only earns its keep because the following statement joins on it, so with nothing
    // to join by it is pure cost on the load side.
    [Fact]
    public void Does_not_index_when_there_is_nothing_to_join_on()
        => StagingPrelude.ShouldIndex(1_000_000, joinColumns: 0, threshold: 5_000).ShouldBeFalse();

    [Fact]
    public void A_zero_threshold_disables_indexing_entirely()
        => StagingPrelude.ShouldIndex(1_000_000, joinColumns: 2, threshold: 0).ShouldBeFalse();
}
