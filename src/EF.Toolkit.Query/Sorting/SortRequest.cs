using System.Diagnostics.CodeAnalysis;

namespace EFToolkit.Query.Sorting;

/// <summary>
///     An ordering as the caller asked for it: a list of field names and directions, not yet checked
///     against any <see cref="SortSpecification{T}" />.
/// </summary>
/// <remarks>
///     This is the shape an API surface receives — a <c>?sort=</c> query-string value, or a list off a
///     request body. It carries no expressions and no knowledge of the entity, so it is safe to
///     accept from an untrusted caller; nothing here reaches the model until a
///     <see cref="SortSpecification{T}" /> resolves it against its allowlist.
/// </remarks>
public sealed record SortRequest
{
    private SortRequest(IReadOnlyList<SortField> fields) => Fields = fields;

    /// <summary>A request that names no ordering, so the specification's default applies.</summary>
    public static SortRequest Empty { get; } = new([]);

    /// <summary>The requested terms, in priority order.</summary>
    public IReadOnlyList<SortField> Fields { get; }

    /// <summary>Whether this request names no terms.</summary>
    public bool IsEmpty => Fields.Count == 0;

    /// <summary>Builds a request from already-parsed terms.</summary>
    /// <param name="fields">The terms, in priority order.</param>
    /// <returns>The request.</returns>
    public static SortRequest From(params IEnumerable<SortField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var materialised = fields.ToArray();
        foreach (var field in materialised)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                throw new ArgumentException("A sort field name cannot be blank.", nameof(fields));
            }
        }

        return materialised.Length == 0 ? Empty : new SortRequest(materialised);
    }

    /// <summary>
    ///     Parses a comma-separated ordering such as <c>"total:desc,placed"</c>. A term may name its
    ///     direction with a <c>:asc</c> / <c>:desc</c> suffix or a leading <c>-</c> for descending;
    ///     with neither it is ascending.
    /// </summary>
    /// <param name="value">
    ///     The ordering to parse. <see langword="null" />, empty or whitespace yields
    ///     <see cref="Empty" />.
    /// </param>
    /// <returns>The parsed request.</returns>
    /// <exception cref="QueryNotSupportedException">A term is malformed.</exception>
    /// <remarks>
    ///     Parsing refuses malformed input rather than skipping it. A request that silently drops the
    ///     term it could not read returns rows in a different order than the caller asked for, and
    ///     the caller has no way to tell.
    /// </remarks>
    public static SortRequest Parse(string? value)
    {
        if (!TryParse(value, out var request, out var error))
        {
            throw new QueryNotSupportedException(error);
        }

        return request;
    }

    /// <summary>Parses an ordering, reporting failure instead of throwing.</summary>
    /// <param name="value">The ordering to parse. See <see cref="Parse" /> for the format.</param>
    /// <param name="request">The parsed request when this returns <see langword="true" />.</param>
    /// <param name="error">Why parsing failed when this returns <see langword="false" />.</param>
    /// <returns><see langword="true" /> when <paramref name="value" /> parsed.</returns>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out SortRequest? request,
        [NotNullWhen(false)] out string? error)
    {
        request = null;
        error = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            request = Empty;
            return true;
        }

        var fields = new List<SortField>();

        foreach (var term in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (term.Length == 0)
            {
                error = $"The sort expression '{value}' contains an empty term. Write the terms as a "
                    + "comma-separated list such as 'total:desc,placed'.";
                return false;
            }

            var parts = term.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length > 2)
            {
                error = $"The sort term '{term}' has more than one direction suffix. Write it as "
                    + "'name', 'name:asc' or 'name:desc'.";
                return false;
            }

            var name = parts[0];
            var direction = SortDirection.Ascending;

            // A leading '-' is the other common spelling of descending, and callers mix the two.
            if (name.StartsWith('-'))
            {
                direction = SortDirection.Descending;
                name = name[1..];
            }
            else if (name.StartsWith('+'))
            {
                name = name[1..];
            }

            if (name.Length == 0)
            {
                error = $"The sort term '{term}' names no field.";
                return false;
            }

            if (parts.Length == 2)
            {
                if (direction == SortDirection.Descending)
                {
                    error = $"The sort term '{term}' names its direction twice, once with '-' and once "
                        + "with a suffix. Use one or the other.";
                    return false;
                }

                switch (parts[1].ToLowerInvariant())
                {
                    case "asc" or "ascending":
                        direction = SortDirection.Ascending;
                        break;
                    case "desc" or "descending":
                        direction = SortDirection.Descending;
                        break;
                    default:
                        error = $"The sort term '{term}' has an unrecognised direction '{parts[1]}'. "
                            + "Use 'asc' or 'desc'.";
                        return false;
                }
            }

            fields.Add(new SortField(name, direction));
        }

        request = new SortRequest(fields);
        return true;
    }

    /// <summary>Renders the request in the form <see cref="Parse" /> accepts.</summary>
    /// <returns>The rendered ordering.</returns>
    public override string ToString() => string.Join(',', Fields);
}
