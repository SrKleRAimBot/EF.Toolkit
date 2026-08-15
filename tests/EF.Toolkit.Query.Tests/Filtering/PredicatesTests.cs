using System.Linq.Expressions;
using EFToolkit.Query.Filtering;
using EFToolkit.Query.Tests.Infrastructure;

namespace EFToolkit.Query.Tests.Filtering;

/// <summary>Covers predicate composition and the parameter rebinding that makes it translatable.</summary>
public class PredicatesTests
{
    private static readonly Order[] Rows =
    [
        new() { Id = 1, Total = 10, Reference = "alpha" },
        new() { Id = 2, Total = 50, Reference = "beta" },
        new() { Id = 3, Total = 90, Reference = "alpha" },
    ];

    [Fact]
    public void And_keeps_only_rows_satisfying_both()
    {
        Expression<Func<Order, bool>> expensive = o => o.Total > 20;
        Expression<Func<Order, bool>> alpha = o => o.Reference == "alpha";

        Matching(expensive.And(alpha)).ShouldBe([3]);
    }

    [Fact]
    public void Or_keeps_rows_satisfying_either()
    {
        Expression<Func<Order, bool>> cheap = o => o.Total < 20;
        Expression<Func<Order, bool>> beta = o => o.Reference == "beta";

        Matching(cheap.Or(beta)).ShouldBe([1, 2]);
    }

    [Fact]
    public void Not_inverts_a_predicate()
    {
        Expression<Func<Order, bool>> alpha = o => o.Reference == "alpha";

        Matching(alpha.Not()).ShouldBe([2]);
    }

    [Fact]
    public void Combining_leaves_one_parameter_and_no_Invoke_node()
    {
        // Composing the delegates instead — x => left(x) && right(x) — compiles and runs in memory,
        // then fails at the provider with an unhandled Invoke node. Splicing the bodies and rebinding
        // the parameter is what keeps the result translatable.
        Expression<Func<Order, bool>> left = o => o.Total > 20;
        Expression<Func<Order, bool>> right = o => o.Reference == "alpha";

        var combined = left.And(right);

        combined.Parameters.ShouldHaveSingleItem();
        combined.ToString().ShouldNotContain("Invoke");

        // Both halves must read from that one parameter, not from the lambda each came from.
        var parameters = new ParameterCollector();
        parameters.Visit(combined.Body);
        parameters.Found.ShouldHaveSingleItem();
        parameters.Found.Single().ShouldBeSameAs(combined.Parameters[0]);
    }

    [Fact]
    public void True_matches_everything_and_False_matches_nothing()
    {
        Matching(Predicates.True<Order>()).ShouldBe([1, 2, 3]);
        Matching(Predicates.False<Order>()).ShouldBeEmpty();
    }

    [Fact]
    public void Chaining_stays_correct_past_two_terms()
    {
        Expression<Func<Order, bool>> a = o => o.Total > 5;
        Expression<Func<Order, bool>> b = o => o.Total < 95;
        Expression<Func<Order, bool>> c = o => o.Reference == "alpha";

        Matching(a.And(b).And(c)).ShouldBe([1, 3]);
        Matching(a.And(b.Or(c))).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Combining_rejects_null_operands()
    {
        Expression<Func<Order, bool>> predicate = o => o.Id > 0;

        Should.Throw<ArgumentNullException>(() => predicate.And(null!));
        Should.Throw<ArgumentNullException>(() => Predicates.Or(null!, predicate));
        Should.Throw<ArgumentNullException>(() => Predicates.Not<Order>(null!));
    }

    private static int[] Matching(Expression<Func<Order, bool>> predicate)
        => Rows.Where(predicate.Compile()).Select(static r => r.Id).ToArray();

    private sealed class ParameterCollector : ExpressionVisitor
    {
        public HashSet<ParameterExpression> Found { get; } = [];

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found.Add(node);
            return base.VisitParameter(node);
        }
    }
}
