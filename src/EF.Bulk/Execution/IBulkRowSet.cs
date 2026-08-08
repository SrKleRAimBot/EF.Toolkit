using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace EFBulk.Execution;

/// <summary>
///     A uniformly-shaped set of rows destined for one table — the unit a bulk executor writes.
/// </summary>
/// <remarks>
///     <para>
///         Two implementations exist, and the distinction is the whole point of the abstraction.
///         Transparent <c>SaveChanges()</c> supplies rows backed by the modification commands EF
///         Core already built. The explicit <c>BulkInsert</c> API supplies rows read straight off
///         the entities, skipping command materialisation, dependency graph construction and
///         topological sorting — work that measurement showed to be roughly 70% of a transparent
///         save's cost and which the explicit API can safely take responsibility for itself.
///     </para>
///     <para>
///         Executors are written once against this interface and serve both.
///     </para>
/// </remarks>
public interface IBulkRowSet
{
    /// <summary>Schema of the target table, or <see langword="null" /> for the default schema.</summary>
    string? Schema { get; }

    /// <summary>Name of the target table.</summary>
    string TableName { get; }

    /// <summary>Whether these rows are being inserted, updated or deleted.</summary>
    EntityState EntityState { get; }

    /// <summary>What the database is being asked to do with these rows.</summary>
    BulkOperationKind Operation { get; }

    /// <summary>Number of rows.</summary>
    int RowCount { get; }

    /// <summary>The columns involved, in a stable order.</summary>
    IReadOnlyList<BulkColumnInfo> Columns { get; }

    /// <summary>Reads the value of <paramref name="column" /> at <paramref name="row" />.</summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    /// <returns>The CLR value, before any value converter has been applied.</returns>
    object? GetValue(int row, int column);

    /// <summary>
    ///     Reads the value of <paramref name="column" /> at <paramref name="row" /> as it was
    ///     loaded from the database.
    /// </summary>
    /// <remarks>
    ///     A concurrency token needs both of its values at once: the loaded value locates the row,
    ///     and the new value is what gets written. Returning only one would make optimistic
    ///     concurrency impossible to express.
    /// </remarks>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    object? GetOriginalValue(int row, int column);

    /// <summary>
    ///     Writes a store-generated value back, so the caller's entities and EF's change tracker end
    ///     up in the state a normal save would have left them in.
    /// </summary>
    /// <param name="row">Zero-based row index.</param>
    /// <param name="column">Index into <see cref="Columns" />.</param>
    /// <param name="value">The value the database generated.</param>
    void SetGeneratedValue(int row, int column, object? value);

    /// <summary>
    ///     The tracked entries behind <paramref name="row" />, for building an exception that names
    ///     the entities involved.
    /// </summary>
    /// <remarks>
    ///     Empty for the explicit bulk API, which works from detached objects and has no entries to
    ///     report.
    /// </remarks>
    /// <param name="row">Zero-based row index.</param>
    IReadOnlyList<IUpdateEntry> GetEntries(int row);
}
