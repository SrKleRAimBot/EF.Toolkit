using System.Linq.Expressions;

namespace EFToolkit.Query.Filtering;

/// <summary>Wraps a runtime value so EF treats it as a parameter rather than a literal.</summary>
/// <remarks>
///     A bare <see cref="Expression.Constant(object)" /> is emitted straight into the SQL, so a query
///     built for one value produces differently-worded SQL from the same query built for another, and
///     the server's plan cache fills with one entry per value. Reading the value off a property of a
///     constant is exactly the shape the C# compiler gives a captured local, which is what EF's
///     parameter extraction looks for — so the generated SQL is the same as if the caller had written
///     the comparison by hand against a variable.
/// </remarks>
internal static class CapturedValue
{
    internal static MemberExpression Of(object? value, Type valueType)
    {
        var boxType = typeof(Box<>).MakeGenericType(valueType);
        var box = Activator.CreateInstance(boxType)!;
        var property = boxType.GetProperty(nameof(Box<>.Value))!;
        property.SetValue(box, value);

        return Expression.Property(Expression.Constant(box, boxType), property);
    }

    private sealed class Box<TValue>
    {
        public TValue? Value { get; set; }
    }
}
