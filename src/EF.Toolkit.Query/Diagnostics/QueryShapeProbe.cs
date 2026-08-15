using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Query.Diagnostics;

/// <summary>What the advisor can tell about a query from its expression tree alone.</summary>
/// <param name="OrderingPaths">The ordering columns, in priority order.</param>
/// <param name="EqualityPaths">Columns constrained to a single value by a <c>Where</c>.</param>
/// <param name="HasInclude">Whether the query pulls in a navigation.</param>
/// <param name="IsSplitQuery">Whether the query was marked <c>AsSplitQuery</c>.</param>
internal sealed record QueryShape(
    IReadOnlyList<string> OrderingPaths,
    IReadOnlyList<string> EqualityPaths,
    bool HasInclude,
    bool IsSplitQuery);

/// <summary>Reads the shape of a query off its expression tree.</summary>
/// <remarks>
///     Best-effort by design. Anything it cannot recognise it leaves out, so the advisor under-reports
///     rather than inventing findings — a false "missing index" against a query the probe misread
///     would train people to ignore the whole feature.
/// </remarks>
internal static class QueryShapeProbe
{
    internal static QueryShape Inspect(Expression expression)
    {
        var ordering = new List<string>();
        var equality = new List<string>();
        var hasInclude = false;
        var isSplitQuery = false;

        // Walked outermost-inward, so the operators are seen in reverse of how they were written.
        var collectingOrder = true;
        var node = expression;

        while (node is MethodCallExpression call && call.Arguments.Count > 0)
        {
            switch (call.Method.Name)
            {
                case "OrderBy" or "OrderByDescending" when collectingOrder:
                    AddPath(ordering, call);

                    // An OrderBy discards whatever ordering preceded it — anything further in is at
                    // best a tiebreaker behind this one, so stop looking.
                    collectingOrder = false;
                    break;

                case "ThenBy" or "ThenByDescending" when collectingOrder:
                    AddPath(ordering, call);
                    break;

                case "Where":
                    CollectEqualityPaths(call, equality);
                    break;

                case "Include" or "ThenInclude":
                    hasInclude = true;
                    break;

                case "AsSplitQuery":
                    isSplitQuery = true;
                    break;

                default:
                    break;
            }

            node = call.Arguments[0];
        }

        ordering.Reverse();

        return new QueryShape(ordering, equality, hasInclude, isSplitQuery);
    }

    private static void AddPath(List<string> paths, MethodCallExpression call)
    {
        if (call.Arguments.Count > 1 && Unquote(call.Arguments[1]) is LambdaExpression lambda
            && TryResolvePath(lambda.Body, lambda.Parameters[0]) is { } path)
        {
            paths.Add(path);
        }
    }

    /// <summary>
    ///     Records columns a <c>Where</c> pins to one value. Only <c>==</c> operands joined by
    ///     <c>&amp;&amp;</c> count: those are the ones an index can satisfy as a leading prefix before
    ///     the ordering columns, which is the shape the coverage check looks for.
    /// </summary>
    private static void CollectEqualityPaths(MethodCallExpression call, List<string> paths)
    {
        if (call.Arguments.Count < 2 || Unquote(call.Arguments[1]) is not LambdaExpression lambda)
        {
            return;
        }

        var parameter = lambda.Parameters[0];
        var pending = new Stack<Expression>();
        pending.Push(lambda.Body);

        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso or ExpressionType.And } and:
                    pending.Push(and.Left);
                    pending.Push(and.Right);
                    break;

                case BinaryExpression { NodeType: ExpressionType.Equal } equal:
                    var path = TryResolvePath(equal.Left, parameter)
                        ?? TryResolvePath(equal.Right, parameter);

                    if (path is not null && !paths.Contains(path))
                    {
                        paths.Add(path);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static Expression Unquote(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Quote } quote
            ? quote.Operand
            : expression;

    private static string? TryResolvePath(Expression expression, ParameterExpression parameter)
    {
        var body = expression;

        while (body is UnaryExpression
               {
                   NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
               } conversion)
        {
            body = conversion.Operand;
        }

        var segments = new Stack<string>();

        while (body is MemberExpression member)
        {
            if (member.Member is not PropertyInfo and not FieldInfo)
            {
                return null;
            }

            segments.Push(member.Member.Name);
            body = member.Expression!;
        }

        return segments.Count > 0 && ReferenceEquals(body, parameter)
            ? string.Join('.', segments)
            : null;
    }
}
