using System.Data.Common;
using EFToolkit.Bulk.Configuration;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFToolkit.Bulk.Execution;

/// <summary>
///     The settings an executor needs while it runs one operation, resolved once at its start.
/// </summary>
/// <remarks>
///     A timeout has three sources of decreasing specificity: the individual call, the context-wide
///     EF.Toolkit.Bulk configuration, and EF's own command timeout. Resolving them in one place is
///     what stops a bulk copy, a staged <c>MERGE</c> and a stock EF batch each running to a
///     different deadline — before this existed the raw commands in the staging paths silently used
///     ADO.NET's 30-second default however the caller had configured the context, so a large staged
///     statement timed out for reasons nothing in the configuration explained.
/// </remarks>
internal readonly record struct BulkExecutionSettings(TimeSpan? Timeout)
{
    /// <summary>Resolves the settings for one operation.</summary>
    /// <param name="rows">The rows being written; carries any per-call setting.</param>
    /// <param name="options">The context-wide EF.Toolkit.Bulk settings.</param>
    /// <param name="connection">The connection, for EF's own configured command timeout.</param>
    public static BulkExecutionSettings Resolve(
        IBulkRowSet rows,
        BulkOptions options,
        IRelationalConnection connection)
        => Resolve(rows.Timeout, options.Timeout, connection.CommandTimeout);

    /// <summary>Resolves the timeout from its three sources, most specific first.</summary>
    /// <param name="perCall">The timeout given to this individual bulk call.</param>
    /// <param name="contextWide">The context's EF.Toolkit.Bulk timeout.</param>
    /// <param name="commandTimeoutSeconds">EF's own command timeout, in seconds.</param>
    public static BulkExecutionSettings Resolve(
        TimeSpan? perCall,
        TimeSpan? contextWide,
        int? commandTimeoutSeconds)
        => new(
            perCall
            ?? contextWide
            ?? (commandTimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null));

    /// <summary>Applies the timeout to <paramref name="command" />, if one was configured.</summary>
    public void Apply(DbCommand command)
    {
        if (Timeout is not null)
        {
            command.CommandTimeout = Seconds(30);
        }
    }

    /// <summary>
    ///     The timeout in whole seconds, or <paramref name="fallback" /> when none was configured.
    /// </summary>
    /// <remarks>
    ///     Every provider reads zero as "no limit", so a sub-second timeout rounds up to one rather
    ///     than down to the exact opposite of what the caller asked for.
    /// </remarks>
    public int Seconds(int fallback)
        => Timeout is { } timeout
            ? (int)Math.Max(1, Math.Ceiling(timeout.TotalSeconds))
            : fallback;
}
