namespace EFBulk.Execution;

/// <summary>
///     What a bulk row set is asking the database to do.
/// </summary>
/// <remarks>
///     Distinct from <see cref="Microsoft.EntityFrameworkCore.EntityState" /> because
///     <see cref="Merge" /> has no entity state: <c>SaveChanges()</c> never produces an upsert, so
///     it only ever arrives through the explicit API.
/// </remarks>
public enum BulkOperationKind
{
    /// <summary>Insert every row.</summary>
    Insert,

    /// <summary>Update rows located by their condition columns.</summary>
    Update,

    /// <summary>Delete rows located by their condition columns.</summary>
    Delete,

    /// <summary>
    ///     Insert rows that do not exist and update those that do, matching on the condition
    ///     columns.
    /// </summary>
    Merge,

    /// <summary>
    ///     Make the table match the supplied rows exactly: insert, update, and delete anything the
    ///     source does not contain.
    /// </summary>
    /// <remarks>
    ///     The delete applies to the whole target table, not to some subset of it. That is the
    ///     defining behaviour of a synchronise and also its main hazard.
    /// </remarks>
    Synchronize
}
