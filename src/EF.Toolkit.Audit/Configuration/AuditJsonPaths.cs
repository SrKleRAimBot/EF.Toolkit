namespace EFToolkit.Audit.Configuration;

/// <summary>
///     What every provider's <c>IndexJsonPath</c> does the same way: name the column, and quote the
///     path for a SQL string literal.
/// </summary>
internal static class AuditJsonPaths
{
    /// <summary>Turns a JSON path into a column name.</summary>
    public static string ColumnName(string path)
    {
        var name = new string([.. path.Where(char.IsLetterOrDigit)]);

        return name.Length == 0 ? "JsonPath" : $"Json_{name}";
    }

    /// <summary>Escapes <paramref name="path" /> for use inside a single-quoted SQL literal.</summary>
    public static string Literal(string path) => path.Replace("'", "''", StringComparison.Ordinal);
}
