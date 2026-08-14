namespace EFToolkit.Audit.Api;

/// <summary>
///     Supplies the primary key of an audit entry.
/// </summary>
/// <typeparam name="TKey">The key type. Whatever this is becomes the audit table's key column.</typeparam>
/// <remarks>
///     <para>
///         The key is pluggable because a codebase that already has an identifier scheme should use
///         it here too, rather than growing a second one for the audit log alone.
///     </para>
///     <para>
///         Prefer a client-generated, time-ordered value. It indexes like a sequence, it is unique
///         across the several <c>DbContext</c>s that may share one audit table, and — because
///         nothing has to be read back from the database — a large audit insert can go down a bulk
///         copy path with no staging table and no sequence reservation.
///     </para>
/// </remarks>
public interface IAuditEntryIdProvider<TKey>
{
    /// <summary>Produces the next identifier.</summary>
    TKey Generate();

    /// <summary>Fills <paramref name="destination" /> with identifiers.</summary>
    /// <param name="destination">The span to fill.</param>
    /// <remarks>
    ///     Override this when a generator can produce a run more cheaply than one at a time — a
    ///     bulk-audited operation asks for as many identifiers as it wrote rows, which can be
    ///     hundreds of thousands.
    /// </remarks>
    void Generate(Span<TKey> destination)
    {
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = Generate();
        }
    }
}
