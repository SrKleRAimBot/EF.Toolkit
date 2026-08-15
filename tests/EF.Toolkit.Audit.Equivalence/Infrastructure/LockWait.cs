using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EFToolkit.Audit.Equivalence.Infrastructure;

/// <summary>
///     Turns "this read is blocked behind an uncommitted transaction" into an error the harness can
///     recognise on sight.
/// </summary>
/// <remarks>
///     A test that reads uncommitted entries from a second connection has two acceptable answers: it
///     sees nothing, or it waits on the writer's locks. Waiting has to be bounded, and the bound has
///     to fail in a way that is distinguishable from every other reason a query can fail. A command
///     timeout is not — it fires just as readily for an audit table that is missing or misnamed, a
///     denied permission, or a broken connection, and treating that as "no entries visible" would
///     let those failures pass as proof of the very thing under test. An engine-level lock timeout
///     is specific: both engines raise a dedicated error for it and for nothing else.
/// </remarks>
internal static class LockWait
{
    /// <summary>
    ///     Bounds how long statements on this connection wait on another transaction's locks.
    /// </summary>
    /// <param name="connection">An open connection, which the setting applies to for its lifetime.</param>
    /// <param name="engine">The fixture's engine name.</param>
    /// <param name="seconds">How long to wait before giving up.</param>
    public static async Task LimitAsync(DbConnection connection, string engine, int seconds)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = engine == "sqlserver"
            ? $"SET LOCK_TIMEOUT {seconds * 1000}"
            : $"SET lock_timeout = '{seconds}s'";

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     True only for the error an engine raises when a statement gave up waiting on a lock.
    /// </summary>
    /// <remarks>
    ///     Everything else — a missing table, a denied permission, a dropped connection — is a real
    ///     failure and must reach the test rather than be read as "not visible".
    /// </remarks>
    public static bool IsTimeout(DbException exception) => exception switch
    {
        // "Lock request time out period exceeded."
        SqlException sqlServer => sqlServer.Number == 1222,
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.LockNotAvailable,
        _ => false
    };
}
