using System.Linq.Expressions;
using EFToolkit.Query.Paging;
using EFToolkit.Query.Sorting;
using EFToolkit.Query.Tests.Infrastructure;

namespace EFToolkit.Query.Tests.Paging;

/// <summary>
///     Covers the lexicographic expansion, which is the single most consequential expression this
///     package builds.
/// </summary>
/// <remarks>
///     Checked two ways. Structurally, because the shape is what makes it translate on every engine;
///     and behaviourally, by compiling the predicate and running it over rows in memory, because the
///     shape being right is not the same as the meaning being right. The behavioural checks compare
///     against the ordering itself: a row passes exactly when it sorts strictly after the boundary.
/// </remarks>
public class KeysetPredicateTests
{
    private static readonly Order[] Rows =
    [
        new() { Id = 1, PlacedAt = new DateTime(2026, 1, 1), Total = 10 },
        new() { Id = 2, PlacedAt = new DateTime(2026, 1, 1), Total = 10 },
        new() { Id = 3, PlacedAt = new DateTime(2026, 1, 1), Total = 20 },
        new() { Id = 4, PlacedAt = new DateTime(2026, 2, 1), Total = 5 },
        new() { Id = 5, PlacedAt = new DateTime(2026, 2, 1), Total = 30 },
    ];

    [Fact]
    public void One_component_is_a_single_comparison()
    {
        var predicate = After(Single(), [3]);

        predicate.Body.NodeType.ShouldBe(ExpressionType.GreaterThan);
        Matching(predicate).ShouldBe([4, 5]);
    }

    [Fact]
    public void Two_components_expand_to_one_OR_of_two_clauses()
    {
        var predicate = After(PlacedThenId(), [new DateTime(2026, 1, 1), 2]);

        predicate.Body.NodeType.ShouldBe(ExpressionType.OrElse);

        var or = (BinaryExpression)predicate.Body;
        or.Left.NodeType.ShouldBe(ExpressionType.GreaterThan);
        or.Right.NodeType.ShouldBe(ExpressionType.AndAlso);
    }

    [Fact]
    public void Three_components_expand_to_three_clauses()
    {
        var predicate = After(PlacedThenTotalThenId(), [new DateTime(2026, 1, 1), 10m, 1]);

        // ((a > a0) || (a == a0 && b > b0)) || (a == a0 && b == b0 && c > c0)
        var outer = (BinaryExpression)predicate.Body;
        outer.NodeType.ShouldBe(ExpressionType.OrElse);
        outer.Left.NodeType.ShouldBe(ExpressionType.OrElse);
        outer.Right.NodeType.ShouldBe(ExpressionType.AndAlso);
    }

    [Fact]
    public void Rows_tied_on_the_leading_column_are_not_dropped()
    {
        // The bug this whole expansion exists to prevent. Written as "a > a0 && b > b0" the boundary
        // row's siblings — same date, higher id — vanish from every page, and nothing in the result
        // says so.
        var predicate = After(PlacedThenId(), [new DateTime(2026, 1, 1), 1]);

        Matching(predicate).ShouldBe([2, 3, 4, 5]);
    }

    [Fact]
    public void The_boundary_row_itself_is_excluded()
        => Matching(After(PlacedThenId(), [new DateTime(2026, 2, 1), 5])).ShouldBeEmpty();

    [Fact]
    public void A_descending_leading_column_reverses_only_that_comparison()
    {
        // Ordered PlacedAt desc, Id asc, the rows after (2026-02-01, 4) are the older dates, plus
        // Id 5 which shares the boundary date and sorts after it.
        var keys = KeysetDefinition.For<Order>(k => k
            .Descending(o => o.PlacedAt)
            .Ascending(o => o.Id));

        Matching(After(keys, [new DateTime(2026, 2, 1), 4])).ShouldBe([1, 2, 3, 5]);
    }

    [Fact]
    public void Reading_backwards_flips_every_comparison()
    {
        var predicate = KeysetPredicate.After<Order>(
            PlacedThenId().Components,
            [new DateTime(2026, 1, 1), 3],
            backward: true);

        Matching(predicate).ShouldBe([1, 2]);
    }

    [Fact]
    public void Walking_the_whole_set_one_row_at_a_time_visits_each_row_exactly_once()
    {
        // The property that actually matters, asserted directly rather than inferred from the shape.
        var keys = PlacedThenId();
        var ordered = Rows.OrderBy(r => r.PlacedAt).ThenBy(r => r.Id).ToArray();
        var visited = new List<int>();
        object?[]? boundary = null;

        while (true)
        {
            var candidates = boundary is null
                ? ordered
                : ordered.Where(After(keys, boundary).Compile()).ToArray();

            if (candidates.Length == 0)
            {
                break;
            }

            var next = candidates[0];
            visited.Add(next.Id);
            boundary = [next.PlacedAt, next.Id];
        }

        visited.ShouldBe(ordered.Select(r => r.Id));
    }

    [Fact]
    public void A_string_column_compares_through_IComparable()
    {
        // string defines no > operator, so this has to route through CompareTo — which EF translates
        // back into a plain SQL comparison against the column.
        var keys = KeysetDefinition.For<Order>(k => k.Ascending(o => o.Reference).Ascending(o => o.Id));
        var predicate = After(keys, ["b", 0]);

        var rows = new[]
        {
            new Order { Id = 1, Reference = "a" },
            new Order { Id = 2, Reference = "b" },
            new Order { Id = 3, Reference = "c" },
        };

        // Id 2 shares the boundary's Reference and sorts after it on the tiebreaker; Id 3 is past the
        // boundary outright. Only Id 1 sorts before it.
        rows.Where(predicate.Compile()).Select(r => r.Id).ShouldBe([2, 3]);
    }

    [Fact]
    public void Boundary_values_are_captured_rather_than_baked_into_the_tree_as_literals()
    {
        // A literal is emitted straight into the SQL, so every cursor would produce differently-worded
        // SQL and the server's plan cache would fill with one entry per page.
        var body = After(Single(), [3]).Body.ToString();

        body.ShouldContain("Value");
        body.ShouldNotContain("3)");
    }

    private static KeysetDefinition<Order> Single()
        => KeysetDefinition.For<Order>(k => k.Ascending(o => o.Id));

    private static KeysetDefinition<Order> PlacedThenId()
        => KeysetDefinition.For<Order>(k => k.Ascending(o => o.PlacedAt).Ascending(o => o.Id));

    private static KeysetDefinition<Order> PlacedThenTotalThenId()
        => KeysetDefinition.For<Order>(k => k
            .Ascending(o => o.PlacedAt)
            .Ascending(o => o.Total)
            .Ascending(o => o.Id));

    private static Expression<Func<Order, bool>> After(
        KeysetDefinition<Order> keys,
        IReadOnlyList<object?> values)
        => KeysetPredicate.After<Order>(keys.Components, values, backward: false);

    private static int[] Matching(Expression<Func<Order, bool>> predicate)
        => Rows.Where(predicate.Compile()).Select(static r => r.Id).Order().ToArray();
}
