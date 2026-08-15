using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace EFToolkit.Query.Paging;

/// <summary>
///     An opaque marker for a position in a keyset-ordered result. Hand it back to
///     <c>ToKeysetPageAsync</c> to read the page that continues from it.
/// </summary>
/// <remarks>
///     <para>
///         Opaque, not secret. The token is Base64Url over a plain-text payload, so anyone holding
///         one can read the key values of the row it points at, and anyone can mint one. It is
///         tamper-<em>evident</em> — a cursor whose fingerprint does not match the ordering it is
///         replayed against is refused — but it is not signed, so it must not carry anything the
///         caller is not already allowed to see, and it must not be treated as authorisation to read
///         the rows it points past.
///     </para>
///     <para>
///         The fingerprint covers the ordering's columns and directions. Replaying a cursor against a
///         different sort is refused rather than silently answered, because the boundary values would
///         be compared against the wrong columns and the page returned would be arbitrary.
///     </para>
/// </remarks>
public sealed record KeysetCursor
{
    private const char Version = '1';
    private const char Separator = '|';

    private string? _token;

    internal KeysetCursor(string fingerprint, KeysetPageDirection direction, IReadOnlyList<string> values)
    {
        Fingerprint = fingerprint;
        Direction = direction;
        Values = values;
    }

    /// <summary>Which way this cursor reads.</summary>
    public KeysetPageDirection Direction { get; }

    internal string Fingerprint { get; }

    internal IReadOnlyList<string> Values { get; }

    /// <summary>The token to hand to a client and receive back.</summary>
    public string Token => _token ??= Encode();

    /// <summary>Reads a token produced by <see cref="Token" />.</summary>
    /// <param name="token">The token.</param>
    /// <returns>The cursor.</returns>
    /// <exception cref="QueryNotSupportedException">The token is not a well-formed cursor.</exception>
    public static KeysetCursor Parse(string token)
    {
        if (!TryParse(token, out var cursor, out var error))
        {
            throw new QueryNotSupportedException(error);
        }

        return cursor;
    }

    /// <summary>Reads a token, reporting failure instead of throwing.</summary>
    /// <param name="token">The token. <see langword="null" /> or blank fails.</param>
    /// <param name="cursor">The cursor when this returns <see langword="true" />.</param>
    /// <param name="error">Why the token was rejected when this returns <see langword="false" />.</param>
    /// <returns><see langword="true" /> when <paramref name="token" /> is a well-formed cursor.</returns>
    /// <remarks>
    ///     Use this on the way in from a client. A malformed cursor is an ordinary bad request, and
    ///     the usual response is to serve the first page rather than to fail.
    /// </remarks>
    public static bool TryParse(
        string? token,
        [NotNullWhen(true)] out KeysetCursor? cursor,
        [NotNullWhen(false)] out string? error)
    {
        cursor = null;
        error = null;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "The cursor is empty.";
            return false;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(token);
        }
        catch (FormatException)
        {
            error = "The cursor is not valid Base64Url, so it did not survive the trip back.";
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(payload);
        }
        catch (ArgumentException)
        {
            error = "The cursor does not decode to text.";
            return false;
        }

        var parts = decoded.Split(Separator);

        if (parts.Length < 3)
        {
            error = "The cursor is truncated.";
            return false;
        }

        if (parts[0].Length != 1 || parts[0][0] != Version)
        {
            error = $"The cursor is version '{parts[0]}', which this version of EF.Toolkit.Query does "
                + $"not read (it writes version '{Version}'). Ask for the first page again.";
            return false;
        }

        var direction = parts[2] switch
        {
            "f" => KeysetPageDirection.Forward,
            "b" => KeysetPageDirection.Backward,
            _ => (KeysetPageDirection?)null,
        };

        if (direction is null)
        {
            error = "The cursor does not say which way it reads.";
            return false;
        }

        var values = new string[parts.Length - 3];
        for (var i = 0; i < values.Length; i++)
        {
            try
            {
                values[i] = Uri.UnescapeDataString(parts[i + 3]);
            }
            catch (UriFormatException)
            {
                error = "A cursor value is not correctly escaped.";
                return false;
            }
        }

        cursor = new KeysetCursor(parts[1], direction.Value, values);
        return true;
    }

    /// <summary>The token, so a cursor interpolates into a URL as itself.</summary>
    /// <returns>The token.</returns>
    public override string ToString() => Token;

    private string Encode()
    {
        var builder = new StringBuilder()
            .Append(Version)
            .Append(Separator)
            .Append(Fingerprint)
            .Append(Separator)
            .Append(Direction == KeysetPageDirection.Forward ? 'f' : 'b');

        foreach (var value in Values)
        {
            // Escaped per value rather than over the whole payload: a key value is free to contain
            // the separator, and a name like "Smith|Jones" would otherwise decode as two components.
            builder.Append(Separator).Append(Uri.EscapeDataString(value));
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(builder.ToString()));
    }
}
