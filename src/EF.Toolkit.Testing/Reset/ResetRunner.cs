namespace EFToolkit.Testing;

/// <summary>Runs a <see cref="ResetScript" />, restoring exactly what its prologue changed.</summary>
/// <remarks>
///     <c>Epilogue[i]</c> undoes <c>Prologue[i]</c>, and runs only if <c>Prologue[i]</c> did: a
///     prologue that fails or is cancelled half way — the second of two <c>NOCHECK CONSTRAINT ALL</c>,
///     say — still restores the first, instead of leaving that table's constraints off in the database.
///     Restores run in reverse, and are not cancellable: abandoning a restore is the one outcome worse
///     than waiting for it.
/// </remarks>
internal static class ResetRunner
{
    internal static async Task RunAsync(
        ResetScript script,
        Func<string, CancellationToken, Task> execute,
        CancellationToken cancellationToken)
    {
        var applied = 0;
        try
        {
            foreach (var sql in script.Prologue)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
                applied++;
            }

            foreach (var sql in script.Body)
            {
                await execute(sql, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            for (var i = applied - 1; i >= 0; i--)
            {
                await execute(script.Epilogue[i], CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static void Run(ResetScript script, Action<string> execute)
    {
        var applied = 0;
        try
        {
            foreach (var sql in script.Prologue)
            {
                execute(sql);
                applied++;
            }

            foreach (var sql in script.Body)
            {
                execute(sql);
            }
        }
        finally
        {
            for (var i = applied - 1; i >= 0; i--)
            {
                execute(script.Epilogue[i]);
            }
        }
    }
}
