using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Query.Diagnostics;

/// <summary>What the advisor can tell about a query from its expression tree alone.</summary>
/// <param name="OrderingPaths">The ordering columns, in priority order.</param>
/// <param name="EqualityPaths">Columns constrained to a single value by a <c>Where</c>.</param>
/// <param name="HasInclude">Whether the query pulls in a navigation.</param>
/// <param name="IsSplitQuery">Whether the query was marked <c>AsSplitQuery</c>.</param>
/// <param name="IgnoresAllQueryFilters">
///     Whether the query called <c>IgnoreQueryFilters()</c> with no arguments, which drops every
///     global query filter on the entity type.
/// </param>
/// <param name="IgnoredQueryFilterKeys">
///     The named filters dropped by <c>IgnoreQueryFilters(["key"])</c>. Empty unless the keyed
///     overload was used.
/// </param>
internal sealed record QueryShape(
    IReadOnlyList<string> OrderingPaths,
    IReadOnlyList<string> EqualityPaths,
    bool HasInclude,
    bool IsSplitQuery,
    bool IgnoresAllQueryFilters = false,
    IReadOnlyList<string>? IgnoredQueryFilterKeys = null)
{
    /// <summary>The named filters this query dropped.</summary>
    public IReadOnlyList<string> IgnoredQueryFilterKeys { get; init; }
        = IgnoredQueryFilterKeys ?? [];
}

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
        var ignoredKeys = new List<string>();
        var hasInclude = false;
        var isSplitQuery = false;
        var ignoresAllFilters = false;

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

                case "IgnoreQueryFilters":
                    // The no-argument overload drops every filter; the EF 10 overload taking filter
                    // keys drops only the ones it names. Anything else about the call is left alone,
                    // and an unreadable key list is treated as naming nothing rather than as naming
                    // all — under-reporting, which is this probe's standing bias.
                    if (call.Arguments.Count == 1)
                    {
                        ignoresAllFilters = true;
                    }
                    else
                    {
                        CollectFilterKeys(call.Arguments[1], ignoredKeys);
                    }

                    break;

                default:
                    break;
            }

            node = call.Arguments[0];
        }

        ordering.Reverse();

        return new QueryShape(
            ordering, equality, hasInclude, isSplitQuery, ignoresAllFilters, ignoredKeys);
    }

    /// <summary>
    ///     Folds the entity type's global query filters into <paramref name="shape" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A global query filter is applied by EF during translation, not by the caller, so it
    ///         never appears in the expression tree the probe walks. Left out, its columns are
    ///         invisible to the index-coverage check — and the single most common filter, a tenant
    ///         column, is exactly the column a well-designed index leads with. The advisor then
    ///         reported a missing index against a query the existing index serves perfectly well.
    ///     </para>
    ///     <para>
    ///         Filters are read from the root type, because EF declares them on the root of a
    ///         hierarchy and applies them to every type in it — a derived type reports none of its
    ///         own. A filter the query dropped with <c>IgnoreQueryFilters</c> is not folded in: it is
    ///         genuinely not in the executed query, and crediting it would put the false finding back
    ///         the other way round.
    ///     </para>
    /// </remarks>
    /// <param name="shape">The shape read off the expression tree.</param>
    /// <param name="entityType">The entity type being queried, if it is known.</param>
    internal static QueryShape IncludingQueryFilters(QueryShape shape, IEntityType? entityType)
    {
        ArgumentNullException.ThrowIfNull(shape);

        if (entityType is null || shape.IgnoresAllQueryFilters)
        {
            return shape;
        }

        var filters = entityType.GetRootType().GetDeclaredQueryFilters();
        if (filters.Count == 0)
        {
            return shape;
        }

        var equality = new List<string>(shape.EqualityPaths);

        foreach (var filter in filters)
        {
            if (filter.Key is { } key && shape.IgnoredQueryFilterKeys.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (filter.Expression is { } lambda)
            {
                CollectEqualityPaths(lambda, equality);
            }
        }

        return equality.Count == shape.EqualityPaths.Count
            ? shape
            : shape with { EqualityPaths = equality };
    }

    /// <summary>Reads the keys out of an <c>IgnoreQueryFilters</c> key list, when they are constant.</summary>
    /// <remarks>
    ///     <c>IgnoreQueryFilters</c> is called as an ordinary method, not inside a lambda, so its key
    ///     list is evaluated before the tree is built and arrives as a constant collection. A key
    ///     list this cannot read is treated as naming nothing rather than as naming all — the
    ///     probe's standing bias, and here the safe direction: an unrecognised list leaves the
    ///     filter credited, which at worst keeps a finding that a clearer call would have raised.
    /// </remarks>
    private static void CollectFilterKeys(Expression argument, List<string> keys)
    {
        switch (argument)
        {
            case ConstantExpression { Value: IEnumerable<string> constant }:
                keys.AddRange(constant);
                break;

            // Nothing observed produces this today, but a provider is free to quote the list rather
            // than fold it, and reading it costs one case.
            case NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array:
                foreach (var element in array.Expressions)
                {
                    if (element is ConstantExpression { Value: string key })
                    {
                        keys.Add(key);
                    }
                }

                break;

            default:
                break;
        }
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

        CollectEqualityPaths(lambda, paths);
    }

    /// <summary>
    ///     Records the columns <paramref name="lambda" /> pins to one value.
    /// </summary>
    /// <remarks>
    ///     Shared by the <c>Where</c> clauses in the caller's tree and by the global query filters
    ///     folded in afterwards, so both are read on identical terms — a filter constrains a column
    ///     in exactly the way a <c>Where</c> does, and an index cannot tell them apart either.
    /// </remarks>
    private static void CollectEqualityPaths(LambdaExpression lambda, List<string> paths)
    {
        if (lambda.Parameters.Count == 0)
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
                    Add(TryResolvePath(equal.Left, parameter)
                        ?? TryResolvePath(equal.Right, parameter));
                    break;

                // A bare bool member and its negation each pin the column to one value just as
                // firmly as `== true` does, and they are how a soft-delete filter is nearly always
                // written: `e => !e.IsDeleted`. Reading only `==` left the column that leads the
                // index out of the shape entirely.
                case UnaryExpression { NodeType: ExpressionType.Not, Operand: var operand }
                    when operand.Type == typeof(bool):
                    Add(TryResolvePath(operand, parameter));
                    break;

                case MemberExpression member when member.Type == typeof(bool):
                    Add(TryResolvePath(member, parameter));
                    break;

                default:
                    break;
            }
        }

        void Add(string? path)
        {
            if (path is not null && !paths.Contains(path))
            {
                paths.Add(path);
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
