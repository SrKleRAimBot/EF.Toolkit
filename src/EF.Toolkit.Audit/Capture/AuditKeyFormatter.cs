using System.Text;

namespace EFToolkit.Audit.Capture;

/// <summary>
///     Renders a row's key as the single string an audit entry is indexed by.
/// </summary>
/// <remarks>
///     One audit table covers every entity type, and their keys have no common type, so the indexed
///     column has to be text. The requirement that follows is that the rendering be injective: two
///     different composite keys must never produce the same string, or a history query returns
///     another row's changes. A string key containing the separator is exactly how that goes wrong,
///     so components are escaped rather than merely joined.
/// </remarks>
internal static class AuditKeyFormatter
{
    private const char Separator = '|';
    private const char Escape = '\\';

    /// <summary>Renders one component.</summary>
    /// <param name="value">The provider value.</param>
    public static string Format(object? value)
        => AuditValues.ToKeyText(value);

    /// <summary>Renders several components as one string.</summary>
    /// <param name="values">The provider values, in key order.</param>
    public static string Format(IReadOnlyList<object?> values)
    {
        if (values.Count == 1)
        {
            return Format(values[0]);
        }

        var builder = new StringBuilder();

        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(Separator);
            }

            Append(builder, AuditValues.ToKeyText(values[i]));
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string component)
    {
        foreach (var character in component)
        {
            if (character is Separator or Escape)
            {
                builder.Append(Escape);
            }

            builder.Append(character);
        }
    }
}
