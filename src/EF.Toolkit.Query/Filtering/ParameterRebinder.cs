using System.Linq.Expressions;

namespace EFToolkit.Query.Filtering;

/// <summary>Rewrites every occurrence of one parameter into another.</summary>
/// <remarks>
///     Two independently written lambdas own two distinct <see cref="ParameterExpression" />
///     instances even when both are spelled <c>x</c>. Splicing their bodies together without
///     rebinding produces a tree referencing a parameter its own lambda does not declare, which EF
///     reports much later as an unhandled expression rather than as the malformed predicate it is.
/// </remarks>
internal sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to)
    : ExpressionVisitor
{
    internal static Expression Rebind(Expression body, ParameterExpression from, ParameterExpression to)
        => ReferenceEquals(from, to) ? body : new ParameterRebinder(from, to).Visit(body);

    protected override Expression VisitParameter(ParameterExpression node)
        => ReferenceEquals(node, from) ? to : base.VisitParameter(node);
}
