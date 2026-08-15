using System.Globalization;

namespace EFToolkit.Query.Paging;

/// <summary>
///     Renders keyset boundary values as text and reads them back, so a cursor survives a round trip
///     through a URL.
/// </summary>
/// <remarks>
///     Every format here is culture-invariant and round-trippable by construction. A cursor issued by
///     one process is decoded by another — often on a different machine with a different locale — and
///     a value that came back a millisecond or an ulp adrift would silently skip or repeat the row
///     sitting on the boundary.
/// </remarks>
internal static class KeysetValueCodec
{
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
            IFormattable formattable => formattable.ToString(null, invariant),
            _ => throw Unsupported(keyType),
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
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
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
            || type == typeof(byte[]);
    }

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
            + "or byte-array type — the primary key is the usual choice.");
}
