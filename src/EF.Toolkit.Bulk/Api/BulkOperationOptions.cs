using System.Linq.Expressions;
using EFToolkit.Bulk.Configuration;

namespace EFToolkit.Bulk.Api;

/// <summary>
///     Per-call settings for an explicit bulk operation.
/// </summary>
public sealed record BulkOperationOptions
{
    /// <summary>Settings used when a bulk method is called without configuration.</summary>
    public static BulkOperationOptions Default { get; } = new();

    /// <summary>
    ///     Whether to attach the entities to the change tracker as <c>Unchanged</c> when the
    ///     operation completes.
    /// </summary>
    /// <remarks>
    ///     Off by default. Store-generated keys are written onto the entities either way — that is
    ///     independent of tracking and costs microseconds, whereas an entry plus an original-values
    ///     snapshot per row costs hundreds of bytes each on exactly the large loads this API exists
    ///     to serve. Entities the context is <em>already</em> tracking are always reconciled,
    ///     regardless of this setting.
    /// </remarks>
    public bool Track { get; init; }

    /// <summary>
    ///     Whether to wrap the operation in a savepoint when it runs inside a transaction the
    ///     caller opened. Defaults to <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     Matches what <c>SaveChanges</c> does, and matters most on PostgreSQL, where a failed
    ///     statement aborts the whole transaction: without a savepoint a caller who catches a bulk
    ///     failure is left holding a transaction that can no longer do anything. Turning it off
    ///     saves a round trip and gives up that recovery.
    /// </remarks>
    public bool Savepoint { get; init; } = true;

    /// <summary>
    ///     Maximum rows sent to the server in one operation. Larger inputs are split.
    ///     When <see langword="null" />, the context-wide setting applies.
    /// </summary>
    public int? BatchSize { get; init; }

    /// <summary>
    ///     Command timeout for this operation. When <see langword="null" />, the context-wide
    ///     setting applies.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Invoked as rows are written, for progress reporting.</summary>
    public Action<BulkProgress>? Progress { get; init; }

    /// <summary>
    ///     Columns a merge matches on. When <see langword="null" />, the primary key is used.
    /// </summary>
    public LambdaExpression? Match { get; init; }

    /// <summary>
    ///     Whether to follow navigations and write the whole reachable graph, principals first.
    /// </summary>
    public bool IncludeGraph { get; init; }

    /// <summary>
    ///     How an upsert works out its insert-versus-update split. When <see langword="null" />,
    ///     the context-wide setting applies.
    /// </summary>
    public MergeCounts? MergeCounts { get; init; }
}

/// <summary>Progress through a bulk operation.</summary>
/// <param name="Completed">Rows written so far.</param>
/// <param name="Total">Rows in the operation.</param>
public readonly record struct BulkProgress(int Completed, int Total)
{
    /// <summary>Fraction complete, between 0 and 1.</summary>
    public double Fraction => Total == 0 ? 1 : (double)Completed / Total;
}
