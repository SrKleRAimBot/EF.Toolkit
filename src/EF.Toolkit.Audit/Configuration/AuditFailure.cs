namespace EFToolkit.Audit.Configuration;

/// <summary>
///     What happens when audit entries cannot be written.
/// </summary>
public enum AuditFailure
{
    /// <summary>
    ///     The failure propagates, and under <see cref="AuditAtomicity.SameTransaction" /> the change
    ///     it described is rolled back with it. The default.
    /// </summary>
    /// <remarks>
    ///     An audit log exists to be complete. One that silently drops entries is worse than one
    ///     that stops the write, because the gap is invisible until somebody needs the entry that
    ///     is not there.
    /// </remarks>
    Throw,

    /// <summary>
    ///     The failure is reported through <c>AuditDiagnostics.SinkFailed</c> and swallowed.
    /// </summary>
    /// <remarks>
    ///     Only appropriate where the audit trail is genuinely advisory. Anything subject to a
    ///     compliance requirement wants <see cref="Throw" />.
    /// </remarks>
    Ignore,
}
