using System.Linq.Expressions;
using System.Reflection;

namespace EFToolkit.Query.Sorting;

/// <summary>One resolved ordering term: the expression to order by and which way round.</summary>
/// <param name="Name">The external field name, or a synthetic one for a tiebreaker.</param>
/// <param name="KeySelector">Selects the key. Its return type is <paramref name="KeyType" />.</param>
/// <param name="KeyType">The key's CLR type, needed to close <c>Queryable.OrderBy</c> over it.</param>
/// <param name="Direction">Which way to order.</param>
/// <param name="PropertyPath">
///     The dotted property path the selector reads, or <see langword="null" /> when the selector is
///     something more involved than a member access chain. Keyset pagination and the index advisor
///     both need this; plain sorting does not.
/// </param>
internal sealed record SortTerm(
    string Name,
    LambdaExpression KeySelector,
    Type KeyType,
    SortDirection Direction,
    string? PropertyPath)
{
    /// <summary>This term ordered the other way.</summary>
    internal SortTerm Reversed() => this with
    {
        Direction = Direction == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending,
    };

    /// <summary>Builds a term from a key selector, resolving its property path where it has one.</summary>
    internal static SortTerm Create(string name, LambdaExpression keySelector, SortDirection direction)
        => new(name, keySelector, keySelector.ReturnType, direction, TryResolvePath(keySelector));

    /// <summary>
    ///     The dotted property path a selector reads, or <see langword="null" /> when the body is not
    ///     a plain member access chain rooted at the lambda's parameter.
    /// </summary>
    /// <remarks>
    ///     Boxing conversions are unwrapped first, so a selector that arrived as
    ///     <c>Expression&lt;Func&lt;T, object&gt;&gt;</c> resolves the same as a strongly typed one.
    /// </remarks>
    private static string? TryResolvePath(LambdaExpression keySelector)
    {
        var body = keySelector.Body;

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

        return segments.Count > 0
            && body is ParameterExpression parameter
            && parameter == keySelector.Parameters[0]
                ? string.Join('.', segments)
                : null;
    }
}
