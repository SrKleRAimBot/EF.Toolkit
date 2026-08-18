using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;

namespace EFToolkit.Query.Paging;

/// <summary>
///     Renders keyset boundary values as text and reads them back, so a cursor survives a round trip
///     through a URL.
/// </summary>
/// <remarks>
///     <para>
///         Every format here is culture-invariant and round-trippable by construction. A cursor issued
///         by one process is decoded by another — often on a different machine with a different locale
///         — and a value that came back a millisecond or an ulp adrift would silently skip or repeat
///         the row sitting on the boundary.
///     </para>
///     <para>
///         The types listed here are the ones a cursor can carry by itself. A type outside the list
///         can still be paged along two other ways: stored through an EF value converter, in which
///         case the cursor carries the <em>provider</em> value and only that has to be on the list; or
///         by supplying a <see cref="TypeConverter" /> that converts to and from a string, which is
///         what NodaTime and most strongly-typed-id generators already do.
///     </para>
/// </remarks>
internal static class KeysetValueCodec
{
    /// <summary>
    ///     The <see cref="TypeConverter" /> for a type, or <see langword="null" /> when it has none
    ///     that reads a string back. Cached because <see cref="TypeDescriptor.GetConverter(Type)" />
    ///     walks attributes and registrations on every call, and a keyset encodes on every page.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, TypeConverter?> TextConverters = new();

    /// <summary>Renders <paramref name="value" /> for a cursor.</summary>
    /// <exception cref="QueryNotSupportedException">The key type has no round-trippable text form.</exception>
    internal static string Encode(object value, Type keyType)
    {
        var type = Nullable.GetUnderlyingType(keyType) ?? keyType;
        var invariant = CultureInfo.InvariantCulture;

        if (type.IsEnum)
        {
            return Convert.ChangeType(value, Enum.GetUnderlyingType(type), invariant)
                is IConvertible underlying
                ? underlying.ToString(invariant)
                : value.ToString()!;
        }

        if (IsNumeric(type) || type == typeof(char))
        {
            return ((IFormattable)value).ToString(null, invariant);
        }

        return value switch
        {
            string s => s,
            bool b => b ? "1" : "0",
            Guid g => g.ToString("N", invariant),
            DateTime dt => dt.ToString("O", invariant),
            DateTimeOffset dto => dto.ToString("O", invariant),
            DateOnly d => d.ToString("O", invariant),
            TimeOnly t => t.ToString("O", invariant),
            TimeSpan ts => ts.ToString("c", invariant),
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => EncodeThroughTypeConverter(value, type),
        };
    }

    /// <summary>Reads a rendered value back as <paramref name="keyType" />.</summary>
    /// <exception cref="QueryNotSupportedException">
    ///     The key type has no round-trippable text form, or the text is not a value of that type —
    ///     which is what a tampered or stale cursor looks like.
    /// </exception>
    internal static object Decode(string raw, Type keyType)
    {
        var type = Nullable.GetUnderlyingType(keyType) ?? keyType;
        var invariant = CultureInfo.InvariantCulture;

        try
        {
            if (type.IsEnum)
            {
                var underlying = Convert.ChangeType(raw, Enum.GetUnderlyingType(type), invariant);
                return Enum.ToObject(type, underlying);
            }

            if (type == typeof(string))
            {
                return raw;
            }

            if (type == typeof(bool))
            {
                // Encode writes "1" or "0" and nothing else, so anything else is a value this codec
                // never issued. Reading it as false rather than refusing it would move the page
                // boundary on a cursor nobody handed out — the one thing decoding is here to prevent.
                return raw switch
                {
                    "1" => true,
                    "0" => false,
                    _ => throw new FormatException(
                        $"'{raw}' is not an encoded boolean; expected '1' or '0'.")
                };
            }

            if (type == typeof(Guid))
            {
                return Guid.ParseExact(raw, "N");
            }

            if (type == typeof(DateTime))
            {
                return DateTime.ParseExact(raw, "O", invariant, DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateTimeOffset))
            {
                return DateTimeOffset.ParseExact(raw, "O", invariant, DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateOnly))
            {
                return DateOnly.ParseExact(raw, "O", invariant);
            }

            if (type == typeof(TimeOnly))
            {
                return TimeOnly.ParseExact(raw, "O", invariant);
            }

            if (type == typeof(TimeSpan))
            {
                return TimeSpan.ParseExact(raw, "c", invariant);
            }

            if (type == typeof(byte[]))
            {
                return Convert.FromBase64String(raw);
            }

            if (type == typeof(char))
            {
                return char.Parse(raw);
            }

            if (IsNumeric(type))
            {
                return Convert.ChangeType(raw, type, invariant);
            }

            if (TextConverterFor(type) is { } converter)
            {
                return converter.ConvertFromInvariantString(raw)
                    ?? throw new FormatException($"'{raw}' converted back to null.");
            }
        }
        catch (Exception ex) when (ex
            is FormatException or OverflowException or ArgumentException or NotSupportedException
            or InvalidCastException)
        {
            throw new QueryNotSupportedException(
                $"A cursor value could not be read back as {type.Name}. The cursor is either from a "
                + "different query or has been altered in transit; ask for the first page again.",
                ex);
        }

        throw Unsupported(keyType);
    }

    /// <summary>Whether a cursor can carry a value of this type at all.</summary>
    internal static bool IsSupported(Type keyType)
    {
        var type = Nullable.GetUnderlyingType(keyType) ?? keyType;

        return type.IsEnum
            || IsNumeric(type)
            || type == typeof(string)
            || type == typeof(bool)
            || type == typeof(char)
            || type == typeof(Guid)
            || type == typeof(DateTime)
            || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly)
            || type == typeof(TimeOnly)
            || type == typeof(TimeSpan)
            || type == typeof(byte[])
            || TextConverterFor(type) is not null;
    }

    /// <summary>
    ///     Renders a value the codec knows nothing about through its <see cref="TypeConverter" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is how a cursor carries a NodaTime <c>Instant</c> or <c>LocalDate</c>: their
    ///         converters write the full-precision ISO-8601 form and read it back exactly, which is
    ///         more than the types' own <c>ToString()</c> does — the general <c>Instant</c> pattern
    ///         truncates to the second, and a boundary rounded down repeats the row it points at on
    ///         the next page.
    ///     </para>
    ///     <para>
    ///         A third-party converter cannot be taken on trust, so the round trip is proved on the
    ///         value being encoded rather than assumed from the contract. It costs one extra parse per
    ///         component per page, against a wrong page that nothing downstream could detect. The
    ///         proof is skipped for a type that has not defined equality, where the comparison would
    ///         be reference identity and would fail on a correct conversion.
    ///     </para>
    /// </remarks>
    private static string EncodeThroughTypeConverter(object value, Type type)
    {
        if (TextConverterFor(type) is not { } converter)
        {
            throw Unsupported(type);
        }

        var text = converter.ConvertToInvariantString(value)
            ?? throw new QueryNotSupportedException(
                $"The TypeConverter for {type.Name} rendered a keyset boundary value as null, so there "
                + "is nothing to put in the cursor. Page along a column of a type whose converter "
                + "writes a string.");

        if (HasValueEquality(type) && !Equals(converter.ConvertFromInvariantString(text), value))
        {
            throw new QueryNotSupportedException(
                $"The TypeConverter for {type.Name} does not round-trip: '{text}' reads back as a "
                + "different value. A cursor is compared against the column it came from, so a "
                + "boundary that shifts on the way out would skip or repeat the row it points at. "
                + "Page along a column of another type, or store this one through an EF value "
                + "converter whose provider type a cursor can carry.");
        }

        return text;
    }

    private static TypeConverter? TextConverterFor(Type type)
        => TextConverters.GetOrAdd(
            type,
            static t =>
            {
                var converter = TypeDescriptor.GetConverter(t);

                // Reading a string back is the direction that has to be claimed: the base
                // TypeConverter — what a type with no converter of its own gets — answers yes to
                // writing one, because everything has a ToString, and no to parsing it back.
                return converter.CanConvertFrom(typeof(string)) && converter.CanConvertTo(typeof(string))
                    ? converter
                    : null;
            });

    /// <summary>
    ///     Whether values of the type compare by their contents. A struct always does; a class only
    ///     once it overrides <see cref="object.Equals(object)" />.
    /// </summary>
    private static bool HasValueEquality(Type type)
        => type.IsValueType
            || type.GetMethod(nameof(Equals), [typeof(object)])?.DeclaringType != typeof(object);

    private static bool IsNumeric(Type type)
        => type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);

    private static QueryNotSupportedException Unsupported(Type keyType)
        => new($"Keyset pagination cannot carry a {keyType.Name} in a cursor, because it has no "
            + "round-trippable text form. Order by a column of a primitive, string, Guid, date, time "
            + "or byte-array type — the primary key is the usual choice — or store this one through a "
            + "value converter, or give it a TypeConverter that reads its own output back.");
}
