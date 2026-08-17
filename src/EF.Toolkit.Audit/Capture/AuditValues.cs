using System.Globalization;
using System.Linq.Expressions;
using EFToolkit.Audit.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Reads property values and puts them in the form the payload records.
/// </summary>
internal static class AuditValues
{
    /// <summary>
    ///     Converts a model value to the form the database stores it in.
    /// </summary>
    /// <remarks>
    ///     An enum mapped to text is recorded as its text, a strongly-typed identifier as its
    ///     underlying value, a <c>DateOnly</c> as whatever the provider makes of it. Recording the
    ///     model value instead would make the trail disagree with the column it describes, and would
    ///     serialize types <see cref="System.Text.Json" /> has no sensible answer for.
    /// </remarks>
    public static object? ToProvider(IProperty property, object? value)
    {
        var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;

        return converter is null ? value : converter.ConvertToProvider(value);
    }

    /// <summary>Whether two model values of <paramref name="property" /> are the same.</summary>
    /// <remarks>
    ///     Through the property's own <c>ValueComparer</c>, not <c>Equals</c>. EF marks a property
    ///     modified when it is assigned at all, including when it is assigned the value it already
    ///     held, so trusting <c>IsModified</c> would fill the trail with updates that changed
    ///     nothing. It also matters for the types where reference equality is the wrong question —
    ///     a <c>byte[]</c>, or a collection mapped through a converter.
    /// </remarks>
    public static bool AreEqual(IProperty property, object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        var comparer = property.GetValueComparer();

        return comparer is null ? Equals(left, right) : comparer.Equals(left, right);
    }

    /// <summary>Renders a provider value as the text that goes into an entry's key column.</summary>
    public static string ToKeyText(object? value)
        => value switch
        {
            null => string.Empty,
            string text => text,
            byte[] bytes => Convert.ToBase64String(bytes),

            // Canonicalized for the reason Canonical sets out: a decimal key read back from the
            // store carries the column's scale, and one read off an entity carries whatever the
            // application supplied, so the same key rendered as text two ways would not match.
            decimal number => Canonical(number).ToString(CultureInfo.InvariantCulture),

            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    /// <summary>
    ///     Strips a decimal's trailing zeros, so the same number always renders the same way.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A <see cref="decimal" /> carries its scale as part of its representation:
    ///         <c>1.5m</c> and <c>1.50m</c> are equal and serialize differently. That is normally
    ///         harmless, but the two capture paths obtain their values from different places. The
    ///         change tracker holds what the application assigned, while a bulk operation reads a
    ///         deleted row's before-image back from the store, where the column's declared scale has
    ///         been applied. The same change then produced <c>"Width": 1.5</c> through one path and
    ///         <c>"Width": 1.50</c> through the other, breaking a byte-identity guarantee over a
    ///         difference that carries no information.
    ///     </para>
    ///     <para>
    ///         Stripping is the direction that invents nothing. Padding to the column's scale would
    ///         mean applying store precision, which this library deliberately does not do — the
    ///         payload records the value, not the column's declaration — and there is no scale to
    ///         pad to when the column does not declare one.
    ///     </para>
    ///     <para>
    ///         Only trailing zeros go. A digit that changes the value stops the reduction, so
    ///         <c>9.99m</c> is left alone and no precision is ever lost.
    ///     </para>
    /// </remarks>
    /// <param name="value">The value to canonicalize.</param>
    /// <returns>The same number, at the smallest scale that represents it exactly.</returns>
    public static decimal Canonical(decimal value)
    {
        // Rounding away a zero leaves the value equal to what it was; rounding away anything else
        // does not, whichever way the midpoint falls. So equality alone decides when to stop, and
        // the loop runs at most once per declared decimal place.
        for (var scale = value.Scale; scale > 0; scale--)
        {
            var reduced = Math.Round(value, scale - 1);

            if (reduced != value)
            {
                return value;
            }

            value = reduced;
        }

        return value;
    }

    /// <summary>Gets, or compiles and caches, a delegate reading <paramref name="property" />.</summary>
    /// <remarks>
    ///     Only needed where there is no change-tracker entry to read from — the explicit bulk API
    ///     works from detached objects. Cached on the property, because compiling an expression tree
    ///     is expensive and a property's accessor never varies.
    /// </remarks>
    public static Func<object, object?>? Getter(IProperty property)
        => property.GetOrAddRuntimeAnnotationValue(
            AuditAnnotations.Getter,
            static p => BuildGetter(p!),
            property);

    private static Func<object, object?>? BuildGetter(IProperty property)
    {
        if (property.PropertyInfo is null && property.FieldInfo is null)
        {
            // A shadow property has no CLR member. Readable through an entry, not through an object.
            return null;
        }

        if (property.DeclaringType is IComplexType)
        {
            // Declared on a complex type, so it is reached through the member holding that value
            // rather than off the entity — the cast below would be to the wrong CLR type. Only key
            // columns are read this way and a complex type has no key, so this is a guard rather
            // than a path anything takes today; it costs one check to keep it that way.
            return null;
        }

        var parameter = Expression.Parameter(typeof(object), "entity");

        // Cast to the declaring type, not to whichever type asked: an inherited property is one
        // IProperty shared by the whole hierarchy, so the accessor cached against it has to be
        // valid for every instance that carries the member.
        var typed = Expression.Convert(parameter, property.DeclaringType.ClrType);

        Expression access = property.PropertyInfo is not null
            ? Expression.Property(typed, property.PropertyInfo)
            : Expression.Field(typed, property.FieldInfo!);

        return Expression
            .Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), parameter)
            .Compile();
    }
}
