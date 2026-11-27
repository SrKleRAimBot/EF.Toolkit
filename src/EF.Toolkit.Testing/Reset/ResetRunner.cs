using System.Runtime.ExceptionServices;

namespace EFToolkit.Testing;

/// <summary>Runs a <see cref="ResetScript" />, as one unit where the engine allows, and restores what it changed.</summary>
/// <remarks>
///     <para>
///         The order is prologue, begin, body, verify, commit. If anything fails after the unit began, it is
///         rolled back first — the rows come back before any check is switched on again, so no
///         half-emptied state is ever committed. Then <c>Epilogue[i]</c> undoes <c>Prologue[i]</c>, for
///         every prologue statement that ran, in reverse.
///     </para>
///     <para>
///         Every cleanup statement is attempted even if an earlier one fails: one table whose
///         constraints cannot be re-enabled is no reason to leave the rest disabled. Cleanup is not
///         cancellable — abandoning it is the one outcome worse than waiting. The original failure is
///         rethrown as it was when cleanup succeeded; when cleanup failed too, all of them are raised
///         together, the original first.
///     </para>
/// </remarks>
internal static class ResetRunner
{
    internal const string VerifyFailed =
        "The reset left rows that violate a foreign key — a trigger on a reset table wrote them while checks "
        + "were suspended. It has been rolled back.";

    internal static string KeptRows(string table)
        => $"The reset left rows in '{table}': a trigger kept them (one that ignores the delete, say). It has been "
            + "rolled back. Exclude the table from the reset, or drop the trigger for it.";

    internal const string AfterCommitFailed =
        "The reset deleted the rows and committed, but restarting the identity counters afterwards failed, "
        + "so some may still continue from where they were rather than from the table's start — see the inner "
        + "exception. On MySQL that needs the ALTER privilege.";

    internal static async Task RunAsync(
        ResetScript script,
        Func<string, CancellationToken, Task> execute,
        Func<string, CancellationToken, Task<long>> count,
        CancellationToken cancellationToken,
        Action? committed = null)
    {
        var applied = 0;
        var begun = false;
        var done = false;
        ExceptionDispatchInfo? failure = null;

        try
        {
            foreach (var sql in script.Prologue)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
                applied++;
            }

            foreach (var sql in script.Begin)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
                begun = true;
            }

            foreach (var sql in script.Body)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
            }

            foreach (var sql in script.Verify)
            {
                if (await count(sql, cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidOperationException(VerifyFailed);
                }
            }

            foreach (var (sql, table) in script.VerifyEmpty)
            {
                if (await count(sql, cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidOperationException(KeptRows(table));
                }
            }

            foreach (var sql in script.Commit)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
            }

            done = true;

            // The rows are gone from here on, whatever follows: the caller learns it now, before a
            // post-commit step that may fail, so a tracker holding the deleted entities is cleared regardless.
            committed?.Invoke();

            try
            {
                foreach (var sql in script.AfterCommit)
                {
                    await execute(sql, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(AfterCommitFailed, e);
            }
        }
#pragma warning disable CA1031 // Captured and rethrown below, after cleanup.
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        var cleanupFailures = new List<Exception>();
        foreach (var sql in Cleanup(script, applied, begun && !done && failure is not null))
        {
            try
            {
                await execute(sql, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Every cleanup statement is attempted; failures are reported together.
            catch (Exception e)
#pragma warning restore CA1031
            {
                cleanupFailures.Add(e);
            }
        }

        Report(failure, cleanupFailures);
    }

    internal static void Run(ResetScript script, Action<string> execute, Func<string, long> count, Action? committed = null)
    {
        var applied = 0;
        var begun = false;
        var done = false;
        ExceptionDispatchInfo? failure = null;

        try
        {
            foreach (var sql in script.Prologue)
            {
                execute(sql);
                applied++;
            }

            foreach (var sql in script.Begin)
            {
                execute(sql);
                begun = true;
            }

            foreach (var sql in script.Body)
            {
                execute(sql);
            }

            foreach (var sql in script.Verify)
            {
                if (count(sql) != 0)
                {
                    throw new InvalidOperationException(VerifyFailed);
                }
            }

            foreach (var (sql, table) in script.VerifyEmpty)
            {
                if (count(sql) != 0)
                {
                    throw new InvalidOperationException(KeptRows(table));
                }
            }

            foreach (var sql in script.Commit)
            {
                execute(sql);
            }

            done = true;

            // The rows are gone from here on, whatever follows: the caller learns it now, before a
            // post-commit step that may fail, so a tracker holding the deleted entities is cleared regardless.
            committed?.Invoke();

            try
            {
                foreach (var sql in script.AfterCommit)
                {
                    execute(sql);
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(AfterCommitFailed, e);
            }
        }
#pragma warning disable CA1031 // Captured and rethrown below, after cleanup.
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = ExceptionDispatchInfo.Capture(e);
        }

        var cleanupFailures = new List<Exception>();
        foreach (var sql in Cleanup(script, applied, begun && !done && failure is not null))
        {
            try
            {
                execute(sql);
            }
#pragma warning disable CA1031 // Every cleanup statement is attempted; failures are reported together.
            catch (Exception e)
#pragma warning restore CA1031
            {
                cleanupFailures.Add(e);
            }
        }

        Report(failure, cleanupFailures);
    }

    private static IEnumerable<string> Cleanup(ResetScript script, int applied, bool rollBack)
    {
        if (rollBack)
        {
            foreach (var sql in script.Rollback)
            {
                yield return sql;
            }
        }

        for (var i = applied - 1; i >= 0; i--)
        {
            yield return script.Epilogue[i];
        }
    }

    private static void Report(ExceptionDispatchInfo? failure, List<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            failure?.Throw();
            return;
        }

        if (failure is null && cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        throw new AggregateException(
            failure is null
                ? "Restoring the connection's settings after a reset failed."
                : "The reset failed, and restoring the connection's settings afterwards failed too.",
            failure is null ? cleanupFailures : [failure.SourceException, .. cleanupFailures]);
    }
}
