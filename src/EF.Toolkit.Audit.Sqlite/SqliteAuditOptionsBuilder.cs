using System.Globalization;
using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.Sqlite;

/// <summary>
///     SQLite-specific auditing settings.
/// </summary>
/// <remarks>
///     SQLite stores JSON as text, so the payload is guarded by a <c>json_valid()</c> check, and has
///     no index over a document. What it has instead is <see cref="IndexJsonPath" />: a stored
///     generated column over one path, with an ordinary index on it.
/// </remarks>
public class SqliteAuditOptionsBuilder : AuditOptionsBuilder
{
    /// <summary>Store types for SQLite.</summary>
    /// <remarks>
    ///     A static instance rather than one built per call, so that two contexts configured
    ///     identically share EF's internal service provider.
    /// </remarks>
    public static AuditStoreTypes StoreTypes { get; } = new()
    {
        Json = "TEXT",

        // jsonb validates on the way in; SQLite's text does not, so the check earns its keep — a
        // payload that is not JSON is unreadable by every json_extract anyone later writes.
        JsonCheck = "json_valid(\"{0}\")",

        // The timestamp and text columns keep EF's own mapping, TEXT: SQLite has no other types to
        // choose between, and EF's format for a DateTimeOffset sorts as text in instant order only
        // while every offset is the same — which the audit clock's UTC timestamps guarantee.
    };

    /// <summary>Initializes a new instance seeded with SQLite's store types.</summary>
    /// <param name="options">The settings to start from.</param>
    /// <remarks>
    ///     SQLite has no schemas — EF Core ignores one, with a warning — so the shared default of
    ///     <c>audit</c> is dropped rather than left to produce that warning on every model build.
    /// </remarks>
    public SqliteAuditOptionsBuilder(AuditOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))) with
        {
            StoreTypes = StoreTypes,
            Schema = options.Schema == AuditOptions.DefaultSchema ? null : options.Schema,
        })
    {
    }

    /// <summary>
    ///     Makes one JSON path searchable, through a stored generated column and an index on it.
    /// </summary>
    /// <param name="path">A JSON path into the payload — <c>$.new.Status</c>.</param>
    /// <param name="storeType">The generated column's type. Defaults to <c>TEXT</c>.</param>
    /// <remarks>
    ///     Each path costs a column on every row, so this is for the handful of paths a trail is
    ///     genuinely searched by, not for everything that might one day be. SQLite cannot add a
    ///     stored generated column to an existing table, so a path added later needs the table
    ///     rebuilt — which EF Core's SQLite migrations do automatically.
    /// </remarks>
    public virtual SqliteAuditOptionsBuilder IndexJsonPath(string path, string storeType = "TEXT")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);

        var existing = Options.StoreTypes.JsonPathIndexes ?? [];

        var indexes = new List<AuditJsonPathIndex>(existing)
        {
            new(
                AuditJsonPaths.ColumnName(path),
                string.Format(
                    CultureInfo.InvariantCulture,
                    "json_extract(\"Changes\", '{0}')",
                    AuditJsonPaths.Literal(path)),
                storeType),
        };

        Options = Options with
        {
            StoreTypes = Options.StoreTypes with { JsonPathIndexes = indexes },
        };

        return this;
    }
}
