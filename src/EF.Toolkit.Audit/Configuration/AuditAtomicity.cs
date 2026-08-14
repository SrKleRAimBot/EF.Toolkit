namespace EFToolkit.Audit.Configuration;

/// <summary>
///     How hard auditing tries to make an audit entry and the change it describes succeed or fail
///     together.
/// </summary>
public enum AuditAtomicity
{
    /// <summary>
    ///     Audit entries are written in the same transaction as the change. The default.
    /// </summary>
    /// <remarks>
    ///     Where a transaction already exists — the caller's, or an ambient
    ///     <c>TransactionScope</c> — that one is used. Where none exists, auditing opens one before
    ///     the save and commits it after the entries are written, which is the only way to make an
    ///     entry that depends on store-generated keys atomic with the rows that generated them.
    /// </remarks>
    SameTransaction,

    /// <summary>
    ///     Audit entries are written after the change, in whatever transaction happens to be open.
    /// </summary>
    /// <remarks>
    ///     Auditing never opens a transaction of its own, so a change saved outside one commits
    ///     before its audit entry is written and the two can diverge if the process dies in
    ///     between. Required for a sink that writes somewhere the change's transaction cannot
    ///     reach — a separate database, a message bus.
    /// </remarks>
    BestEffort,
}
