namespace EFToolkit.Audit.Configuration;

/// <summary>
///     The store types and index settings the audit table is created with.
/// </summary>
/// <remarks>
///     <para>
///         Supplied by the provider package, not by the application. The core package has no opinion
///         on what JSON is called — it is <c>jsonb</c> on PostgreSQL and <c>nvarchar(max)</c> on SQL
///         Server — and every value here is <see langword="null" /> by default, meaning "whatever
///         the provider maps the CLR type to".
///     </para>
///     <para>
///         Index settings arrive as raw annotations rather than as typed calls, so that the core
///         package can apply <c>Npgsql:IndexMethod</c> without referencing Npgsql. Provider packages
///         should expose these as static instances: two contexts configured identically ought to
///         hash identically, and reference equality over a dictionary is what decides that.
///     </para>
/// </remarks>
public sealed record AuditStoreTypes
{
    /// <summary>Provider-neutral defaults: EF's own mapping for every column, and no payload index.</summary>
    public static AuditStoreTypes Default { get; } = new();

    /// <summary>Store type of the change payload — <c>jsonb</c>, <c>nvarchar(max)</c>.</summary>
    public string? Json { get; init; }

    /// <summary>
    ///     A CHECK constraint asserting the payload really is JSON, with <c>{0}</c> standing in for
    ///     the column name.
    /// </summary>
    /// <remarks>
    ///     For engines that store JSON as text and can validate it — SQL Server's <c>ISJSON</c>.
    ///     PostgreSQL needs nothing here: <c>jsonb</c> validates on the way in.
    /// </remarks>
    public string? JsonCheck { get; init; }

    /// <summary>Store type of <c>OccurredAt</c> — <c>timestamptz</c>, <c>datetimeoffset(7)</c>.</summary>
    public string? Timestamp { get; init; }

    /// <summary>Store type of the unbounded text columns — the entity type, key and source.</summary>
    public string? Text { get; init; }

    /// <summary>
    ///     Annotations that turn the payload index into one the engine can actually use, or
    ///     <see langword="null" /> where the engine has no such index.
    /// </summary>
    /// <remarks>
    ///     On PostgreSQL, <c>Npgsql:IndexMethod</c> of <c>gin</c> with <c>jsonb_path_ops</c>
    ///     operators. Without them a plain B-tree index on a <c>jsonb</c> column answers equality on
    ///     the whole document and nothing else, which is not the question anybody asks of an audit
    ///     payload.
    /// </remarks>
    public IReadOnlyDictionary<string, object?>? PayloadIndex { get; init; }

    /// <summary>
    ///     Persisted computed columns over JSON paths, indexed individually.
    /// </summary>
    /// <remarks>
    ///     How an engine with no JSON index makes one path searchable. Configured through the
    ///     provider's own options builder, because both the expression and what it costs are
    ///     particular to that engine.
    /// </remarks>
    public IReadOnlyList<AuditJsonPathIndex>? JsonPathIndexes { get; init; }
}

/// <summary>A computed column over a JSON path, and its index.</summary>
/// <param name="Name">The column name.</param>
/// <param name="ComputedSql">The expression producing it, supplied by the provider package.</param>
/// <param name="StoreType">The column's store type, or <see langword="null" /> for the default.</param>
public sealed record AuditJsonPathIndex(string Name, string ComputedSql, string? StoreType = null);
