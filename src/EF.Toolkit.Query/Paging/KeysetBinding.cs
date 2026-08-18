using EFToolkit.Query.Sorting;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EFToolkit.Query.Paging;

/// <summary>
///     A keyset definition's components resolved against the EF model: what a cursor renders each one
///     as, and how a boundary value gets there and back.
/// </summary>
/// <remarks>
///     <para>
///         A cursor carries the <em>stored</em> value of a converted column, not the CLR one. That is
///         the value <c>ORDER BY</c> and the page comparison run against, and it is the half the model
///         can vouch for: a strongly typed id stored as <c>text</c> is a string as far as the database
///         is concerned, whatever the property's own type has to say about a text form.
///     </para>
///     <para>
///         Which is also why this cannot be decided from the CLR type alone, and so cannot be decided
///         when the definition is built: there is no model at that point, and the converter that maps
///         the property is invisible. A definition built against no model at all — paging a projection
///         — binds every component to its own CLR type, which is the same answer as before.
///     </para>
/// </remarks>
internal sealed class KeysetBinding
{
    private readonly IReadOnlyList<SortTerm> _components;
    private readonly ValueConverter?[] _converters;

    private KeysetBinding(IReadOnlyList<SortTerm> components, ValueConverter?[] converters)
    {
        _components = components;
        _converters = converters;
    }

    /// <summary>Resolves each component against <paramref name="entityType" />.</summary>
    /// <param name="components">The ordering, in priority order.</param>
    /// <param name="entityType">The mapped type, or <see langword="null" /> for a projection.</param>
    internal static KeysetBinding For(IReadOnlyList<SortTerm> components, IEntityType? entityType)
    {
        var converters = new ValueConverter?[components.Count];

        for (var i = 0; i < components.Count; i++)
        {
            converters[i] = entityType?.FindProperty(components[i].PropertyPath!) is { } property
                ? ConverterOf(property)
                : null;
        }

        return new KeysetBinding(components, converters);
    }

    /// <summary>The type a cursor renders the component as.</summary>
    internal Type CursorTypeOf(int index)
        => _converters[index]?.ProviderClrType ?? _components[index].KeyType;

    /// <summary>Renders a component's boundary value, read off a row, for a cursor.</summary>
    internal string Encode(int index, object value)
    {
        if (_converters[index] is not { } converter)
        {
            return KeysetValueCodec.Encode(value, _components[index].KeyType);
        }

        var stored = converter.ConvertToProvider(value)
            ?? throw new QueryNotSupportedException(
                $"The value converter on keyset column '{_components[index].PropertyPath}' turned a "
                + "boundary value into null, so there is nothing to put in the cursor. A keyset "
                + "column has to be stored as a value the cursor can point at.");

        return KeysetValueCodec.Encode(stored, CursorTypeOf(index));
    }

    /// <summary>
    ///     Reads a rendered value back as the component's CLR type, which is the side the page
    ///     comparison is written against — EF converts it back to the stored form when it translates.
    /// </summary>
    internal object Decode(int index, string raw)
    {
        var stored = KeysetValueCodec.Decode(raw, CursorTypeOf(index));

        if (_converters[index] is not { } converter)
        {
            return stored;
        }

        return converter.ConvertFromProvider(stored)
            ?? throw new QueryNotSupportedException(
                $"A cursor value for keyset column '{_components[index].PropertyPath}' converted back "
                + "to null. The cursor is either from a different query or has been altered in "
                + "transit; ask for the first page again.");
    }

    /// <summary>
    ///     The conversion a property is stored through, reading the type mapping as well as the
    ///     explicitly configured converter: <c>HasConversion&lt;string&gt;()</c> leaves nothing on the
    ///     property itself and changes the mapping instead, so checking only the property is how this
    ///     misses the exact case it exists for.
    /// </summary>
    internal static ValueConverter? ConverterOf(IProperty property)
        => property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
}
