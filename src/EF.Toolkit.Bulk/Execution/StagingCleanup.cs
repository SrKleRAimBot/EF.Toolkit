using EFToolkit.Bulk.Diagnostics;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     Drops a staging table without ever letting the attempt mask the exception that is already
///     in flight.
/// </summary>
/// <remarks>
///     <para>
///         Cleanup runs in a <c>finally</c>, so anything it throws <em>replaces</em> the original
///         exception. That is worst exactly when it matters most: a failed statement leaves
///         PostgreSQL's transaction aborted, so the <c>DROP</c> fails too and the caller is told
///         "current transaction is aborted" instead of what actually went wrong.
///     </para>
///     <para>
///         So every exception is caught, not merely the provider's own type — an
///         <see cref="ObjectDisposedException" /> from a broken connection would mask just as
///         effectively as a <c>PostgresException</c>. Swallowing is safe because the explicit drop
///         is defence in depth rather than the only cleanup: a staging table lives in the session,
///         and both providers reset the session when the connection returns to the pool (Npgsql
///         issues <c>DISCARD ALL</c>, SQL Server <c>sp_reset_connection</c>), and a temp table
///         created inside a transaction disappears with the rollback.
///     </para>
///     <para>
///         Swallowed is not the same as hidden: every failure is reported through
///         <see cref="BulkDiagnostics" />, so a genuine leak is observable rather than silent.
///     </para>
/// </remarks>
internal static class StagingCleanup
{
    /// <summary>Runs <paramref name="drop" />, reporting rather than propagating any failure.</summary>
    /// <param name="table">The staging table being dropped, for the diagnostic.</param>
    /// <param name="drop">Performs the drop.</param>
    public static async Task RunAsync(string table, Func<Task> drop)
    {
        try
        {
            await drop().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            BulkDiagnostics.ReportStagingCleanupFailed(table, ex);
        }
    }
}
