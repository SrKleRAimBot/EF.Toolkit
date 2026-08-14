using System.Globalization;
using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.SqlServer;

/// <summary>
///     SQL Server-specific auditing settings.
/// </summary>
/// <remarks>
///     SQL Server has no index over a JSON document, so the payload column gets none. What it has
///     instead is <see cref="IndexJsonPath" />: a persisted computed column over one path, with an
///     ordinary index on it. That answers one question very well and every other question not at
///     all, which is the trade to be aware of when moving a trail between engines.
/// </remarks>
public class SqlServerAuditOptionsBuilder : AuditOptionsBuilder
{
    /// <summary>Store types for SQL Server.</summary>
    /// <remarks>
    ///     A static instance rather than one built per call, so that two contexts configured
    ///     identically share EF's internal service provider.
    /// </remarks>
    public static AuditStoreTypes StoreTypes { get; } = new()
    {
        Json = "nvarchar(max)",

        // jsonb validates on the way in; nvarchar does not, so the check earns its keep — a payload
        // that is not JSON is unreadable by every query anyone will later write against it.
        JsonCheck = "ISJSON([{0}]) = 1",
        Timestamp = "datetimeoffset(7)",

        // Bounded, because these columns are indexed and nvarchar(max) cannot be. 256 characters
        // holds a rendered composite key comfortably.
        Text = "nvarchar(256)",
    };

    /// <summary>Initializes a new instance seeded with SQL Server's store types.</summary>
    /// <param name="options">The settings to start from.</param>
    public SqlServerAuditOptionsBuilder(AuditOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))) with
        {
            StoreTypes = StoreTypes,
        })
    {
    }

    /// <summary>
    ///     Makes one JSON path searchable, through a persisted computed column and an index on it.
    /// </summary>
    /// <param name="path">A JSON path into the payload — <c>$.new.Status</c>.</param>
    /// <param name="storeType">The computed column's type. Defaults to <c>nvarchar(256)</c>.</param>
    /// <remarks>
    ///     Each path costs a column on every row, so this is for the handful of paths a trail is
    ///     genuinely searched by, not for everything that might one day be.
    /// </remarks>
    public virtual SqlServerAuditOptionsBuilder IndexJsonPath(
        string path,
        string storeType = "nvarchar(256)")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);

        var existing = Options.StoreTypes.JsonPathIndexes ?? [];

        var indexes = new List<AuditJsonPathIndex>(existing)
        {
            new(
                ColumnName(path),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "JSON_VALUE([Changes], '{0}')",
                    path.Replace("'", "''", StringComparison.Ordinal)),
                storeType),
        };

        Options = Options with
        {
            StoreTypes = Options.StoreTypes with { JsonPathIndexes = indexes },
        };

        return this;
    }

    /// <summary>Turns a JSON path into a column name.</summary>
    private static string ColumnName(string path)
    {
        var name = new string([.. path.Where(char.IsLetterOrDigit)]);

        return name.Length == 0 ? "JsonPath" : $"Json_{name}";
    }
}
