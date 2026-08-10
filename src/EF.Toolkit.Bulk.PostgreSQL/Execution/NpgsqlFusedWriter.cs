using System.Linq.Expressions;
using System.Reflection;
using EFToolkit.Bulk.Execution;
using Npgsql;

namespace EFToolkit.Bulk.PostgreSQL.Execution;

/// <summary>
///     Compiles one column into a single delegate that goes from an entity straight to the copy
///     stream, without the value ever becoming an <see cref="object" />.
/// </summary>
/// <remarks>
///     <para>
///         The boxed path reads a property into an <see cref="object" />, converts it, hands it to
///         a writer, and the writer unboxes it again for a strongly-typed driver call. Every step
///         is necessary in isolation and the whole sequence is not: for a value-typed column the
///         box is allocated and discarded once per cell, which on a thirty-column table at a
///         hundred thousand rows is millions of allocations doing nothing.
///     </para>
///     <para>
///         Fusing them removes the box entirely. The getter, the value converter's own expression
///         and the typed <c>WriteAsync&lt;T&gt;</c> call are compiled into one lambda, so the value
///         travels from the property to the driver in its own type. The entity stays
///         <see cref="object" /> — one cast, a predictable branch, and nothing allocated.
///     </para>
///     <para>
///         This is available only to the explicit API. The transparent path reads from EF's
///         modification commands, where the value is an <see cref="object" /> before this library
///         sees it, and no interface design recovers that.
///     </para>
/// </remarks>
internal static class NpgsqlFusedWriter
{
    private static readonly MethodInfo WriteNull =
        typeof(NpgsqlBinaryImporter).GetMethod(
            nameof(NpgsqlBinaryImporter.WriteNullAsync), [typeof(CancellationToken)])!;

    /// <summary>
    ///     Builds a fused writer for <paramref name="column" />, or <see langword="null" /> when
    ///     the column cannot take this path.
    /// </summary>
    /// <remarks>
    ///     Declining is per column, never per table: one exotic type must not cost the other
    ///     twenty-nine their fast path.
    /// </remarks>
    /// <param name="column">The column to write.</param>
    /// <param name="entityClrType">CLR type of the entities being read.</param>
    public static Func<NpgsqlBinaryImporter, object, CancellationToken, Task>? TryBuild(
        BulkColumnInfo column,
        Type entityClrType)
    {
        if (!NpgsqlColumnWriter.IsSafeToInferType(column))
        {
            return null;
        }

        var member = (MemberInfo?)column.Property?.PropertyInfo ?? column.Property?.FieldInfo;
        if (member is null || !member.DeclaringType!.IsAssignableFrom(entityClrType))
        {
            // A shadow property, or one declared somewhere this entity does not reach.
            return null;
        }

        var importer = Expression.Parameter(typeof(NpgsqlBinaryImporter), "importer");
        var entity = Expression.Parameter(typeof(object), "entity");
        var token = Expression.Parameter(typeof(CancellationToken), "token");

        Expression value = Expression.MakeMemberAccess(
            Expression.Convert(entity, entityClrType), member);

        if (column.TypeMapping?.Converter is { } converter)
        {
            // Inlined rather than invoked: the converter's own expression becomes part of this
            // lambda, so there is no delegate call and no boxing across the boundary.
            var conversion = converter.ConvertToProviderExpression;
            value = new ParameterSubstitution(conversion.Parameters[0], value).Visit(conversion.Body);
        }

        var providerType = column.ProviderClrType;

        var write = typeof(NpgsqlBinaryImporter)
            .GetMethods()
            .FirstOrDefault(IsTypedWriteAsync)
            ?.MakeGenericMethod(providerType);

        if (write is null)
        {
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(value.Type);
        Expression body;

        if (value.Type.IsValueType && underlying is null)
        {
            // Cannot be null, so there is no branch to pay for.
            body = Expression.Call(importer, write, Coerce(value, providerType), token);
        }
        else
        {
            var local = Expression.Variable(value.Type, "value");
            var payload = underlying is not null
                ? Expression.Property(local, "Value")
                : (Expression)local;

            body = Expression.Block(
                [local],
                Expression.Assign(local, value),
                Expression.Condition(
                    Expression.Equal(local, Expression.Constant(null, value.Type)),
                    Expression.Call(importer, WriteNull, token),
                    Expression.Call(importer, write, Coerce(payload, providerType), token)));
        }

        try
        {
            return Expression
                .Lambda<Func<NpgsqlBinaryImporter, object, CancellationToken, Task>>(
                    body, importer, entity, token)
                .Compile();
        }
        catch (InvalidOperationException)
        {
            // A shape the tree could not be built for. The boxed writer handles it correctly, just
            // more slowly, which is a better outcome than failing the operation.
            return null;
        }
    }

    private static Expression Coerce(Expression value, Type target)
        => value.Type == target ? value : Expression.Convert(value, target);

    private static bool IsTypedWriteAsync(MethodInfo method)
    {
        if (method.Name != nameof(NpgsqlBinaryImporter.WriteAsync) || !method.IsGenericMethodDefinition)
        {
            return false;
        }

        var parameters = method.GetParameters();

        // The overload taking only the value and a cancellation token — the one that lets Npgsql
        // resolve the handler from T rather than looking it up per value by store type name.
        return parameters.Length == 2
            && parameters[0].ParameterType.IsGenericParameter
            && parameters[1].ParameterType == typeof(CancellationToken);
    }

    private sealed class ParameterSubstitution(ParameterExpression parameter, Expression replacement)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == parameter ? replacement : base.VisitParameter(node);
    }
}
