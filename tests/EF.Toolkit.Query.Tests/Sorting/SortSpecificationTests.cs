using EFToolkit.Query.Sorting;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

// Shouldly declares a SortDirection of its own, and the global usings bring both into scope.
using SortDirection = EFToolkit.Query.Sorting.SortDirection;

namespace EFToolkit.Query.Tests.Sorting;

/// <summary>Covers the allowlist and the total-ordering guarantee.</summary>
public class SortSpecificationTests
{
    private static SortSpecification<Order> Standard() => SortSpecification.For<Order>(s => s
        .Allow("placed", o => o.PlacedAt)
        .Allow("total", o => o.Total)
        .DefaultOrder("placed", SortDirection.Descending)
        .Tiebreaker(o => o.Id));

    [Fact]
    public void An_unknown_field_is_refused_and_the_message_lists_what_is_allowed()
    {
        // The field name comes off a query string, so a client-side typo would otherwise become a
        // silently different ordering that neither side can see.
        var failure = Should.Throw<QueryNotSupportedException>(
            () => Standard().Resolve(SortRequest.Parse("secret")));

        failure.Message.ShouldContain("secret");
        failure.Message.ShouldContain("placed");
        failure.Message.ShouldContain("total");
    }

    [Fact]
    public void Field_names_are_matched_case_insensitively()
        => Standard().Resolve(SortRequest.Parse("TOTAL"))[0].Name.ShouldBe("total");

    [Fact]
    public void Naming_the_same_field_twice_is_refused()
        => Should.Throw<QueryNotSupportedException>(
            () => Standard().Resolve(SortRequest.Parse("total:asc,total:desc")));

    [Fact]
    public void An_empty_request_uses_the_default_ordering()
    {
        var terms = Standard().Resolve(SortRequest.Empty);

        terms[0].PropertyPath.ShouldBe(nameof(Order.PlacedAt));
        terms[0].Direction.ShouldBe(SortDirection.Descending);
    }

    [Fact]
    public void A_null_request_uses_the_default_ordering()
        => Standard().Resolve(null)[0].PropertyPath.ShouldBe(nameof(Order.PlacedAt));

    [Fact]
    public void The_tiebreaker_is_appended_to_every_ordering()
    {
        // This is the guarantee the whole type exists for: without a unique final term, two rows that
        // tie can land on either side of a page boundary and be returned twice or not at all.
        var terms = Standard().Resolve(SortRequest.Parse("total"));

        terms.Count.ShouldBe(2);
        terms[^1].PropertyPath.ShouldBe(nameof(Order.Id));
    }

    [Fact]
    public void The_tiebreaker_is_not_appended_twice_when_it_was_asked_for_explicitly()
    {
        var specification = SortSpecification.For<Order>(s => s
            .Allow("id", o => o.Id)
            .Allow("total", o => o.Total)
            .DefaultOrder("total")
            .Tiebreaker(o => o.Id));

        var terms = specification.Resolve(SortRequest.Parse("total,id:desc"));

        terms.Count.ShouldBe(2);
        terms[^1].PropertyPath.ShouldBe(nameof(Order.Id));

        // The caller's direction wins — appending a second Id term would be dead weight in the SQL
        // and would contradict what they asked for.
        terms[^1].Direction.ShouldBe(SortDirection.Descending);
    }

    [Fact]
    public void The_requested_direction_overrides_the_declared_one()
        => Standard().Resolve(SortRequest.Parse("total:desc"))[0].Direction
            .ShouldBe(SortDirection.Descending);

    [Fact]
    public void A_specification_allowing_nothing_is_refused()
        => Should.Throw<QueryNotSupportedException>(
            () => SortSpecification.For<Order>(static _ => { }));

    [Fact]
    public void A_specification_with_neither_default_nor_tiebreaker_is_refused()
    {
        // Such a specification would answer an empty request with an unordered query, and paginating
        // an unordered query is exactly the bug this package exists to prevent.
        var failure = Should.Throw<QueryNotSupportedException>(
            () => SortSpecification.For<Order>(s => s.Allow("total", o => o.Total)));

        failure.Message.ShouldContain("Tiebreaker");
    }

    [Fact]
    public void A_tiebreaker_alone_is_enough_to_build()
        => SortSpecification.For<Order>(s => s.Allow("total", o => o.Total).Tiebreaker(o => o.Id))
            .HasTiebreaker.ShouldBeTrue();

    [Fact]
    public void A_default_naming_a_field_that_is_not_allowed_is_refused()
        => Should.Throw<QueryNotSupportedException>(
            () => SortSpecification.For<Order>(s => s
                .Allow("total", o => o.Total)
                .DefaultOrder("placed")));

    [Fact]
    public void Declaring_the_same_field_twice_is_refused()
        => Should.Throw<QueryNotSupportedException>(
            () => SortSpecification.For<Order>(s => s
                .Allow("total", o => o.Total)
                .Allow("TOTAL", o => o.Id)
                .Tiebreaker(o => o.Id)));

    [Fact]
    public void Applying_a_specification_produces_ordinary_EF_ordering()
    {
        using var context = TestModel.Context();

        var ordered = context.Orders.OrderBy(Standard(), "total:desc");

        // Rendered rather than executed: what matters is that the ordering reached the provider as
        // OrderByDescending/ThenBy, indistinguishable from hand-written LINQ.
        var rendered = ordered.Expression.ToString();
        rendered.ShouldContain("OrderByDescending");
        rendered.ShouldContain("ThenBy");
    }

    [Fact]
    public void AllowedFields_reports_what_a_caller_may_ask_for()
        => Standard().AllowedFields.Order(StringComparer.Ordinal).ShouldBe(["placed", "total"]);

    [Fact]
    public void Apply_rejects_a_null_source()
        => Should.Throw<ArgumentNullException>(() => Standard().Apply(null!));

    [Fact]
    public void For_rejects_a_null_configuration()
        => Should.Throw<ArgumentNullException>(() => SortSpecification.For<Order>(null!));
}
