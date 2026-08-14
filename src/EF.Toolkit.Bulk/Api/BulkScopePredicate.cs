using System.Globalization;
using System.Linq.Expressions;
using EFToolkit.Bulk.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Turns a <c>WithinScope</c> lambda into the SQL predicate a synchronise's delete arm runs
///     under.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately a small, closed translator rather than a general one. It accepts
///         <c>&amp;&amp;</c>-ed comparisons between a mapped property and a value — which covers what
///         scoping is actually for: a tenant, a partition, an import batch — and rejects everything
///         else with a message naming the expression it could not take. A predicate that is either
///         understood exactly or refused outright is the property that matters here, because this
///         predicate is the only thing standing between a synchronise and the rest of the table.
///     </para>
///     <para>
///         Splicing <c>IQueryable.ToQueryString()</c> would have covered far more of LINQ, and is
///         not an option: EF emits a <c>DECLARE</c> preamble and its own parameter names, neither of
///         which survives being embedded in someone else's statement.
///     </para>
///     <para>
///         Every value becomes a parameter, so nothing a caller supplies is ever concatenated into
///         SQL.
///     </para>
/// </remarks>
internal static class BulkScopePredicate
{
    /// <summary>Translates <paramref name="predicate" /> against <paramref name="entityType" />.</summary>
    /// <param name="entityType">The entity type being synchronised.</param>
    /// <param name="predicate">The scope lambda.</param>
    /// <param name="sqlHelper">Delimits column identifiers for the provider.</param>
    /// <param name="alias">The alias the statement gives the target table.</param>
    public static BulkScope Translate(
        IEntityType entityType,
        LambdaExpression predicate,
        ISqlGenerationHelper sqlHelper,
        string alias)
    {
        var context = new Context(
            predicate.Parameters[0], ColumnsOf(entityType), sqlHelper, alias, []);

        var sql = Visit(predicate.Body, context);

        return new BulkScope(sql, context.Values);
    }

    /// <summary>
    ///     Turns an interpolated string into a scope, its holes becoming parameters.
    /// </summary>
    /// <remarks>
    ///     The escape hatch for a predicate the lambda translator does not cover. It is raw SQL by
    ///     design, but it is not string concatenation: the interpolation holes are the values, and
    ///     they are bound rather than pasted, so the usual injection risk of a raw-SQL overload is
    ///     not present.
    /// </remarks>
    public static BulkScope Translate(FormattableString sql)
    {
        var placeholders = new object[sql.ArgumentCount];
        for (var i = 0; i < placeholders.Length; i++)
        {
            placeholders[i] = "@" + BulkScope.ParameterName(i);
        }

        return new BulkScope(
            string.Format(CultureInfo.InvariantCulture, sql.Format, placeholders),
            sql.GetArguments());
    }

    private static string Visit(Expression node, Context context)
    {
        node = Unwrap(node);

        switch (node)
        {
            case BinaryExpression { NodeType: ExpressionType.AndAlso } and:
                return $"({Visit(and.Left, context)} AND {Visit(and.Right, context)})";

            case BinaryExpression binary when Operator(binary.NodeType) is { } comparison:
                return Compare(binary, comparison, context);

            // A bare bool property, and its negation: 'c => c.IsActive' reads better than
            // 'c => c.IsActive == true' and means the same thing.
            case UnaryExpression { NodeType: ExpressionType.Not } negation
                when ColumnOf(Unwrap(negation.Operand), context) is { } negated:
                return Equality(negated, "=", false, context);

            case MemberExpression member when ColumnOf(member, context) is { } column
                && column.Property.ClrType == typeof(bool):
                return Equality(column, "=", true, context);

            default:
                throw Unsupported(node);
        }
    }

    private static string Compare(BinaryExpression binary, string comparison, Context context)
    {
        var left = Unwrap(binary.Left);
        var right = Unwrap(binary.Right);

        Column? column;
        Expression value;

        if ((column = ColumnOf(left, context)) is not null)
        {
            value = right;
        }
        else if ((column = ColumnOf(right, context)) is not null)
        {
            // The comparison is written the other way round, so the operator turns with it.
            value = left;
            comparison = Mirror(comparison);
        }
        else
        {
            throw Unsupported(binary);
        }

        if (References(value, context.Parameter))
        {
            throw new BulkNotSupportedException(
                "WithinScope compares a property to another property of the same entity, which is "
                + $"not supported: '{binary}'. The scope selects rows of the target table, so the "
                + "value side has to be something this call already knows.");
        }

        return Equality(column.Value, comparison, Evaluate(value), context);
    }

    private static string Equality(Column column, string comparison, object? value, Context context)
    {
        var reference = $"{context.Alias}.{context.SqlHelper.DelimitIdentifier(column.Name)}";

        if (value is null)
        {
            return comparison switch
            {
                "=" => $"{reference} IS NULL",
                "<>" => $"{reference} IS NOT NULL",
                _ => throw new BulkNotSupportedException(
                    $"WithinScope cannot compare '{column.Name}' to null with '{comparison}'. Only "
                    + "equality and inequality have a meaning against null.")
            };
        }

        value = Coerce(value, column);

        context.Values.Add(
            column.TypeMapping?.Converter is { } converter
                ? converter.ConvertToProvider(value)
                : value);

        return $"{reference} {comparison} @{BulkScope.ParameterName(context.Values.Count - 1)}";
    }

    /// <summary>
    ///     Puts the value back into the form the property declares, before any converter runs.
    /// </summary>
    /// <remarks>
    ///     An enum comparison reaches the expression tree already lowered to its underlying type —
    ///     <c>(int)r.Status == 1</c> — so a converter built for the enum would be handed an
    ///     <see cref="int" /> and reject it. Widening happens the same way for a comparison against
    ///     a literal of a different numeric type.
    /// </remarks>
    private static object Coerce(object value, Column column)
    {
        var target = Nullable.GetUnderlyingType(column.Property.ClrType)
            ?? column.Property.ClrType;

        if (target.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            return target.IsEnum
                ? Enum.ToObject(target, value)
                : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException)
        {
            throw new BulkNotSupportedException(
                $"WithinScope compares '{column.Name}' to a {value.GetType().Name}, which cannot be "
                + $"read as the {target.Name} the property holds.",
                exception);
        }
    }

    private static Column? ColumnOf(Expression node, Context context)
        => Unwrap(node) is MemberExpression member
            && member.Expression == context.Parameter
            && context.Columns.TryGetValue(member.Member.Name, out var column)
                ? column
                : null;

    /// <summary>Reads a value the predicate closed over, or a literal.</summary>
    private static object? Evaluate(Expression node)
        => node is ConstantExpression constant
            ? constant.Value
            : Expression
                .Lambda<Func<object?>>(Expression.Convert(node, typeof(object)))
                .Compile()
                .Invoke();

    private static bool References(Expression node, ParameterExpression parameter)
        => new ParameterFinder(parameter).Found(node);

    /// <summary>Strips the boxing conversion a value-typed member picks up on the way in.</summary>
    private static Expression Unwrap(Expression node)
        => node is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert
            ? Unwrap(convert.Operand)
            : node;

    private static string? Operator(ExpressionType type)
        => type switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "<>",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => null
        };

    private static string Mirror(string comparison)
        => comparison switch
        {
            ">" => "<",
            ">=" => "<=",
            "<" => ">",
            "<=" => ">=",
            _ => comparison
        };

    private static BulkNotSupportedException Unsupported(Expression node)
        => new(
            $"WithinScope cannot translate '{node}'. It accepts comparisons between a mapped "
            + "property and a value, combined with '&&' — for example "
            + "'c => c.TenantId == tenantId'. For anything else, use the WithinScope overload that "
            + "takes an interpolated string, whose holes are bound as parameters.");

    private static Dictionary<string, Column> ColumnsOf(IEntityType entityType)
    {
        var mapping = entityType.GetTableMappings().FirstOrDefault()
            ?? throw new BulkNotSupportedException(
                $"'{entityType.DisplayName()}' is not mapped to a table, so a scope has nothing to "
                + "select rows of.");

        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);

        foreach (var columnMapping in mapping.ColumnMappings)
        {
            columns[columnMapping.Property.Name] = new Column(
                columnMapping.Column.Name,
                columnMapping.Column.StoreTypeMapping,
                columnMapping.Property);
        }

        return columns;
    }

    private readonly record struct Column(
        string Name,
        RelationalTypeMapping? TypeMapping,
        IProperty Property);

    private sealed record Context(
        ParameterExpression Parameter,
        Dictionary<string, Column> Columns,
        ISqlGenerationHelper SqlHelper,
        string Alias,
        List<object?> Values);

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly ParameterExpression _parameter;
        private bool _found;

        public ParameterFinder(ParameterExpression parameter) => _parameter = parameter;

        public bool Found(Expression node)
        {
            _found = false;
            Visit(node);
            return _found;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            _found |= node == _parameter;
            return node;
        }
    }
}
