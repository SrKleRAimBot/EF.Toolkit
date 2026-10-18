using System.Globalization;
using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.MySql;

/// <summary>
///     MySQL-specific auditing settings.
/// </summary>
/// <remarks>
///     MySQL's <c>JSON</c> type validates on the way in and stores a parsed binary form, so the
///     payload needs no check constraint — but there is no index over a whole JSON document. What
///     there is instead is <see cref="IndexJsonPath" />: a stored generated column over one path,
///     with an ordinary index on it. That answers one question very well and every other question
///     not at all, which is the trade to be aware of when moving a trail between engines.
/// </remarks>
public class MySqlAuditOptionsBuilder : AuditOptionsBuilder
{
    /// <summary>Store types for MySQL.</summary>
    /// <remarks>
    ///     A static instance rather than one built per call, so that two contexts configured
    ///     identically share EF's internal service provider.
    /// </remarks>
    public static AuditStoreTypes StoreTypes { get; } = new()
    {
        // Validated and parsed on the way in, like jsonb: a payload that is not JSON is refused by
        // the server rather than stored to be unreadable later.
        Json = "json",

        // MySQL has no type that keeps an offset, so the instant is what is stored, in UTC, to the
        // microsecond — the finest DATETIME holds. Without the precision a provider's default for
        // a DateTimeOffset is whole seconds, and two changes in the same second lose their order.
        Timestamp = "datetime(6)",

        // Bounded, because these columns are indexed and TEXT can only be indexed by prefix. 256
        // characters holds a rendered composite key comfortably, and the history index stays inside
        // InnoDB's 3072-byte key limit even at four bytes a character.
        Text = "varchar(256)",
    };

    /// <summary>Initializes a new instance seeded with MySQL's store types.</summary>
    /// <param name="options">The settings to start from.</param>
    /// <remarks>
    ///     The table goes in the context's own database unless <c>Schema(...)</c> says otherwise. A
    ///     schema on MySQL is a separate database, which the application's account may not be
    ///     allowed to create and its backups may not include — so the shared default of
    ///     <c>audit</c> would be a surprising one here.
    /// </remarks>
    public MySqlAuditOptionsBuilder(AuditOptions options)
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
    /// <param name="storeType">The generated column's type. Defaults to <c>varchar(256)</c>.</param>
    /// <remarks>
    ///     <para>
    ///         Each path costs a column on every row, so this is for the handful of paths a trail is
    ///         genuinely searched by, not for everything that might one day be.
    ///     </para>
    ///     <para>
    ///         The value is unquoted, so a string at the path is stored as the string itself rather
    ///         than as its JSON spelling — <c>Shipped</c>, not <c>"Shipped"</c> — which is what an
    ///         equality filter on the column expects.
    ///     </para>
    /// </remarks>
    public virtual MySqlAuditOptionsBuilder IndexJsonPath(
        string path,
        string storeType = "varchar(256)")
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
                    "JSON_UNQUOTE(JSON_EXTRACT(`Changes`, '{0}'))",
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
