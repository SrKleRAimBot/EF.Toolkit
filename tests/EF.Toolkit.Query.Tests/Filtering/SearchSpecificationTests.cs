using EFToolkit.Query.Filtering;
using EFToolkit.Query.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EFToolkit.Query.Tests.Filtering;

/// <summary>Covers the free-text search allowlist and its blank-term behaviour.</summary>
public class SearchSpecificationTests
{
    private static readonly Customer[] Rows =
    [
        new() { Id = 1, Name = "Alice Anderson", Email = "alice@example.com" },
        new() { Id = 2, Name = "Bob Brown", Email = null },
        new() { Id = 3, Name = "Carol Clark", Email = "anderson@example.com" },
    ];

    private static SearchSpecification<Customer> ByNameAndEmail(
        SearchMatch match = SearchMatch.Contains)
        => SearchSpecification.For<Customer>(s => s
            .Field(c => c.Name)
            .Field(c => c.Email)
            .Match(match));

    [Fact]
    public void A_term_matches_across_every_declared_field()
        => Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "nderson")).ShouldBe([1, 3]);

    [Fact]
    public void Matching_is_left_to_the_evaluator_rather_than_forced_case_insensitive()
    {
        // In memory this is an ordinal comparison; against a database it follows the column's
        // collation, which is usually case-insensitive. Forcing a ToLower() here would make the
        // library disagree with the database and, worse, would stop any index serving the predicate.
        Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "anderson")).ShouldBe([3]);
        Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "Anderson")).ShouldBe([1]);
    }

    [Fact]
    public void StartsWith_anchors_the_match()
        => Ids(Rows.AsQueryable().Search(ByNameAndEmail(SearchMatch.StartsWith), "Alice")).ShouldBe([1]);

    [Fact]
    public void Exact_requires_the_whole_field()
    {
        Ids(Rows.AsQueryable().Search(ByNameAndEmail(SearchMatch.Exact), "Bob Brown")).ShouldBe([2]);
        Ids(Rows.AsQueryable().Search(ByNameAndEmail(SearchMatch.Exact), "Bob")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_term_applies_no_filter(string? term)
    {
        // What an empty search box sends. The expected answer is the unfiltered list, not an empty
        // one, and not an exception.
        var source = Rows.AsQueryable();

        source.Search(ByNameAndEmail(), term).ShouldBeSameAs(source);
        ByNameAndEmail().Build(term).ShouldBeNull();
    }

    [Fact]
    public void A_null_field_does_not_throw_when_the_predicate_runs_in_memory()
    {
        // Bob's email is null. Server-side EF's null semantics exclude the row; the explicit guard
        // makes the in-memory evaluation agree instead of throwing.
        Should.NotThrow(() => Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "example.com")));
        Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "example.com")).ShouldBe([1, 3]);
    }

    [Fact]
    public void A_term_matching_nothing_returns_nothing()
        => Ids(Rows.AsQueryable().Search(ByNameAndEmail(), "zzz")).ShouldBeEmpty();

    [Fact]
    public void A_specification_covering_no_fields_is_refused()
        => Should.Throw<QueryNotSupportedException>(
                () => SearchSpecification.For<Customer>(static _ => { }))
            .Message.ShouldContain("no fields");

    [Fact]
    public void FieldCount_reports_what_the_search_covers()
        => ByNameAndEmail().FieldCount.ShouldBe(2);

    [Fact]
    public void Search_rejects_null_arguments()
    {
        Should.Throw<ArgumentNullException>(() => Rows.AsQueryable().Search(null!, "x"));
        Should.Throw<ArgumentNullException>(
            () => SearchSpecification.For<Customer>(s => s.Field(null!)));
    }

    private static int[] Ids(IQueryable<Customer> query)
        => query.Select(static c => c.Id).ToArray();
}
