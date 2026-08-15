using EFToolkit.Query.Configuration;
using EFToolkit.Query.Paging;

namespace EFToolkit.Query.Tests.Paging;

/// <summary>Covers defaulting, clamping and the arithmetic that turns a page number into an offset.</summary>
public class PageRequestTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_negative_page_number_is_refused_at_construction(int pageNumber)
        => Should.Throw<ArgumentOutOfRangeException>(() => PageRequest.Of(pageNumber));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_non_positive_page_size_is_refused_at_construction(int pageSize)
        => Should.Throw<ArgumentOutOfRangeException>(() => PageRequest.Of(1, pageSize));

    [Fact]
    public void An_unset_page_size_takes_the_configured_default()
    {
        var options = QueryOptions.Default with { DefaultPageSize = 25 };

        PageRequest.Of(1).Resolve(options).PageSize.ShouldBe(25);
    }

    [Fact]
    public void A_named_page_size_is_kept_even_when_it_equals_the_default()
    {
        // "The caller did not say" and "the caller asked for the number that happens to be the
        // default" have to stay distinguishable, or raising the default silently changes the size a
        // client explicitly requested.
        var request = PageRequest.Of(1, 20);

        request.PageSize.ShouldBe(20);
        request.Resolve(QueryOptions.Default with { DefaultPageSize = 50 }).PageSize.ShouldBe(20);
    }

    [Fact]
    public void An_oversized_page_is_clamped_rather_than_refused()
    {
        // The value usually arrives from a query string. A ceiling that throws turns ?pageSize=1000000
        // into a way to generate 500s; a ceiling that clamps just serves a smaller page.
        var options = QueryOptions.Default with { MaxPageSize = 100 };
        var page = PageRequest.Of(1, 1_000_000).Resolve(options);

        page.PageSize.ShouldBe(100);
        page.WasClamped.ShouldBeTrue();
    }

    [Fact]
    public void A_page_within_the_ceiling_is_not_reported_as_clamped()
        => PageRequest.Of(1, 50).Resolve(QueryOptions.Default).WasClamped.ShouldBeFalse();

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 20)]
    [InlineData(5, 80)]
    public void One_based_numbering_offsets_from_page_one(int pageNumber, int expectedOffset)
        => PageRequest.Of(pageNumber, 20).Resolve(QueryOptions.Default).Offset.ShouldBe(expectedOffset);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 20)]
    [InlineData(4, 80)]
    public void Zero_based_numbering_offsets_from_page_zero(int pageNumber, int expectedOffset)
    {
        var options = QueryOptions.Default with { Numbering = PageNumbering.ZeroBased };

        PageRequest.Of(pageNumber, 20).Resolve(options).Offset.ShouldBe(expectedOffset);
    }

    [Fact]
    public void Page_zero_is_refused_under_one_based_numbering()
    {
        var failure = Should.Throw<QueryNotSupportedException>(
            () => PageRequest.Of(0).Resolve(QueryOptions.Default));

        failure.Message.ShouldContain("OneBased");
        failure.Message.ShouldContain("ZeroBased");
    }

    [Fact]
    public void A_page_number_near_int_max_is_refused_rather_than_overflowing_into_the_wrong_page()
    {
        // Computed in int, (int.MaxValue - 1) * 20 wraps to a small positive offset and quietly reads
        // a page from near the start of the table.
        var failure = Should.Throw<QueryNotSupportedException>(
            () => PageRequest.Of(int.MaxValue, 20).Resolve(QueryOptions.Default with { MaxPageSize = 20 }));

        failure.Message.ShouldContain("ToKeysetPageAsync");
    }

    [Fact]
    public void The_largest_offset_that_still_fits_is_accepted()
        => PageRequest.Of(int.MaxValue, 1).Resolve(QueryOptions.Default with { MaxPageSize = 1 })
            .Offset.ShouldBe(int.MaxValue - 1);
}
