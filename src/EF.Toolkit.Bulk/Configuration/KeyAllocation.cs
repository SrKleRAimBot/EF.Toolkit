namespace EFToolkit.Bulk.Configuration;

/// <summary>
///     Controls how EF.Toolkit.Bulk obtains values for store-generated keys when inserting.
/// </summary>
public enum KeyAllocation
{
    /// <summary>
    ///     Reserve a block of key values up front and write them directly in the bulk copy stream.
    ///     This is a single extra round trip, needs no read-back, and correlates rows to entities
    ///     exactly.
    ///     <para>
    ///         On PostgreSQL this is also the only rigorous option before PostgreSQL 17:
    ///         <c>RETURNING</c> cannot reference the staging table, so a staged insert has no
    ///         documented way to map generated keys back to source rows.
    ///     </para>
    ///     <para>
    ///         Falls back to <see cref="Staging" /> automatically when reservation is impossible —
    ///         identity columns with no reachable sequence, <c>GENERATED ALWAYS AS IDENTITY</c>,
    ///         triggers, or insufficient permissions on the sequence.
    ///     </para>
    /// </summary>
    ReserveBlocks,

    /// <summary>
    ///     Always bulk-copy into a staging table and let the server assign keys, correlating rows
    ///     back by an ordinal column. One uniform code path and no sequence gaps, at the cost of an
    ///     extra write pass over the data.
    ///     <para>
    ///         Correlation is exact on SQL Server (<c>MERGE ... OUTPUT inserted.Id, src.__ord</c>)
    ///         and on PostgreSQL 17+ (<c>MERGE ... RETURNING</c>). On earlier PostgreSQL versions
    ///         EF.Toolkit.Bulk correlates on an alternate key if the entity has one, and otherwise throws
    ///         rather than depend on undocumented <c>RETURNING</c> row ordering.
    ///     </para>
    /// </summary>
    Staging
}
