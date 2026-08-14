using EFToolkit.Audit.Configuration;

namespace EFToolkit.Audit.PostgreSQL;

/// <summary>
///     PostgreSQL-specific auditing settings.
/// </summary>
/// <remarks>
///     Exists so that knobs which only mean something on PostgreSQL appear only when this package
///     is installed, matching how EF.Toolkit.Bulk's provider options builders are shaped.
/// </remarks>
public class NpgsqlAuditOptionsBuilder : AuditOptionsBuilder
{
    /// <summary>The operator class the payload index uses unless told otherwise.</summary>
    /// <remarks>
    ///     Smaller than the default GIN operator class, because it indexes paths rather than every
    ///     key and value separately. It supports containment — <c>@&gt;</c> — which is the operator
    ///     an audit payload is actually searched with, and drops the existence operators, which it
    ///     is not.
    /// </remarks>
    public const string DefaultOperators = "jsonb_path_ops";

    /// <summary>Store types and index settings for PostgreSQL.</summary>
    /// <remarks>
    ///     A static instance rather than one built per call: two contexts configured identically
    ///     should share EF's internal service provider, and reference equality over the index
    ///     annotations is part of what decides that.
    /// </remarks>
    public static AuditStoreTypes StoreTypes { get; } = Types(DefaultOperators);

    /// <summary>Initializes a new instance seeded with PostgreSQL's store types.</summary>
    /// <param name="options">The settings to start from.</param>
    public NpgsqlAuditOptionsBuilder(AuditOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))) with
        {
            StoreTypes = StoreTypes,
        })
    {
    }

    /// <summary>Chooses the operator class of the payload's GIN index.</summary>
    /// <param name="operators">
    ///     <c>jsonb_path_ops</c> by default, or <see langword="null" /> for the default GIN operator
    ///     class — larger, and the one to use if the trail is searched with the key-existence
    ///     operators <c>?</c>, <c>?|</c> and <c>?&amp;</c> rather than with containment.
    /// </param>
    public virtual NpgsqlAuditOptionsBuilder GinOperators(string? operators)
    {
        Options = Options with { StoreTypes = Types(operators) };
        return this;
    }

    private static AuditStoreTypes Types(string? operators)
    {
        var index = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Npgsql:IndexMethod"] = "gin",
        };

        if (operators is not null)
        {
            index["Npgsql:IndexOperators"] = new[] { operators };
        }

        return new AuditStoreTypes
        {
            // jsonb rather than json: it is parsed once on the way in, so it is comparable,
            // indexable and cheap to read a path out of. json is text with a validity check.
            Json = "jsonb",
            Timestamp = "timestamptz",
            Text = "text",
            PayloadIndex = index,
        };
    }
}
