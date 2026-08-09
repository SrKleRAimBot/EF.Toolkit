namespace EFToolkit.Bulk.Execution;

/// <summary>
///     The outcome of offering a partition to an <see cref="IBulkOperationExecutor" />.
/// </summary>
/// <remarks>
///     Executors can only decide some things by asking the database — whether an identity column
///     has a reachable sequence, whether it is <c>GENERATED ALWAYS</c>, whether a trigger is
///     attached. Those answers are not available to the synchronous, connectionless
///     <see cref="IBulkOperationExecutor.CanExecute" />, so an executor may also decline once it
///     has a connection in hand.
///     <para>
///         An executor that declines must not have modified anything, since the partition will then
///         be replayed through stock EF Core. In practice that means every such check happens before
///         the first row is written.
///     </para>
/// </remarks>
public readonly record struct BulkExecutionResult
{
    private BulkExecutionResult(
        bool handled,
        int rowsAffected,
        string? declinedReason,
        int inserted = 0,
        int updated = 0,
        int deleted = 0)
    {
        Handled = handled;
        RowsAffected = rowsAffected;
        DeclinedReason = declinedReason;
        Inserted = inserted;
        Updated = updated;
        Deleted = deleted;
    }

    /// <summary>Rows deleted, when the operation was a synchronise.</summary>
    public int Deleted { get; }

    /// <summary>Rows inserted, when the operation was a merge.</summary>
    public int Inserted { get; }

    /// <summary>Rows updated, when the operation was a merge.</summary>
    public int Updated { get; }

    /// <summary>Whether the executor performed the write.</summary>
    public bool Handled { get; }

    /// <summary>Rows affected, when <see cref="Handled" /> is <see langword="true" />.</summary>
    public int RowsAffected { get; }

    /// <summary>Why the executor declined, when <see cref="Handled" /> is <see langword="false" />.</summary>
    public string? DeclinedReason { get; }

    /// <summary>The partition was written by the executor.</summary>
    /// <param name="rowsAffected">Number of rows written.</param>
    public static BulkExecutionResult Executed(int rowsAffected)
        => new(true, rowsAffected, null);

    /// <summary>The partition was merged, splitting into inserts and updates.</summary>
    /// <param name="inserted">Rows that did not previously exist.</param>
    /// <param name="updated">Rows that did.</param>
    /// <param name="deleted">Rows removed because the source did not contain them.</param>
    public static BulkExecutionResult Merged(int inserted, int updated, int deleted = 0)
        => new(true, inserted + updated + deleted, null, inserted, updated, deleted);

    /// <summary>
    ///     The executor cannot handle this partition and has changed nothing; it must be replayed
    ///     through EF Core.
    /// </summary>
    /// <param name="reason">Why, for diagnostics and error messages.</param>
    public static BulkExecutionResult Declined(string reason)
        => new(false, 0, reason);
}
