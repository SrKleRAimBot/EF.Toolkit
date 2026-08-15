using EFToolkit.Query.Sorting;

// Shouldly declares a SortDirection of its own, and the global usings bring both into scope.
using SortDirection = EFToolkit.Query.Sorting.SortDirection;

namespace EFToolkit.Query.Tests.Sorting;

/// <summary>Covers parsing the sort expression that arrives on a query string.</summary>
public class SortRequestTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_expression_means_the_default_ordering(string? value)
        => SortRequest.Parse(value).IsEmpty.ShouldBeTrue();

    [Fact]
    public void A_bare_name_is_ascending()
    {
        var request = SortRequest.Parse("total");

        request.Fields.ShouldHaveSingleItem();
        request.Fields[0].ShouldBe(new SortField("total", SortDirection.Ascending));
    }

    [Theory]
    [InlineData("total:desc")]
    [InlineData("total:DESC")]
    [InlineData("total:descending")]
    [InlineData("-total")]
    public void Descending_can_be_spelled_four_ways(string value)
        => SortRequest.Parse(value).Fields[0].Direction.ShouldBe(SortDirection.Descending);

    [Theory]
    [InlineData("total:asc")]
    [InlineData("total:ascending")]
    [InlineData("+total")]
    public void Ascending_can_be_spelled_three_ways(string value)
        => SortRequest.Parse(value).Fields[0].Direction.ShouldBe(SortDirection.Ascending);

    [Fact]
    public void Terms_keep_the_order_they_were_written_in()
    {
        var request = SortRequest.Parse(" total:desc , placed , -reference ");

        request.Fields.ShouldBe(
        [
            new SortField("total", SortDirection.Descending),
            new SortField("placed", SortDirection.Ascending),
            new SortField("reference", SortDirection.Descending),
        ]);
    }

    [Theory]
    [InlineData("total,,placed")]
    [InlineData("total:desc:asc")]
    [InlineData("total:sideways")]
    [InlineData("-total:desc")]
    [InlineData("-")]
    [InlineData(":desc")]
    public void Malformed_terms_are_refused_rather_than_skipped(string value)
    {
        // Skipping the term nobody could read returns rows in an order the caller did not ask for,
        // and there is nothing in the response for them to notice it by.
        Should.Throw<QueryNotSupportedException>(() => SortRequest.Parse(value));

        SortRequest.TryParse(value, out var request, out var error).ShouldBeFalse();
        request.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_parsed_request_renders_back_to_something_that_parses_the_same()
    {
        const string original = "total:desc,placed";
        var round = SortRequest.Parse(SortRequest.Parse(original).ToString());

        round.Fields.ShouldBe(SortRequest.Parse(original).Fields);
    }

    [Fact]
    public void From_rejects_a_blank_field_name()
        => Should.Throw<ArgumentException>(
            () => SortRequest.From(new SortField("  ", SortDirection.Ascending)));

    [Fact]
    public void From_with_no_fields_is_the_empty_request()
        => SortRequest.From().ShouldBeSameAs(SortRequest.Empty);
}
