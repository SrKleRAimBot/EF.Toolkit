using System.Diagnostics;
using EFBulk.Planning;

namespace EFBulk.Diagnostics;

/// <summary>
///     Diagnostic events emitted by EF.Bulk.
/// </summary>
/// <remarks>
///     <para>
///         These exist because <see cref="Microsoft.EntityFrameworkCore.Diagnostics.IDbCommandInterceptor" />
///         cannot see bulk writes: <c>COPY</c> and <c>SqlBulkCopy</c> are not
///         <see cref="System.Data.Common.DbCommand" />s, so an application relying on command
///         interception for logging would go blind exactly where the work moved.
///     </para>
///     <para>
///         Subscribe with <see cref="DiagnosticListener.AllListeners" />, matching
///         <see cref="ListenerName" />. Every payload is a strongly-typed record, and events are
///         only constructed when something is listening.
///     </para>
/// </remarks>
public static class BulkDiagnostics
{
    /// <summary>Name of the <see cref="DiagnosticListener" /> EF.Bulk publishes to.</summary>
    public const string ListenerName = "EFBulk";

    /// <summary>
    ///     Raised once per batch when its partitions have been planned. Payload:
    ///     <see cref="PartitionsPlannedEvent" />.
    /// </summary>
    public const string PartitionsPlanned = "EFBulk.PartitionsPlanned";

    /// <summary>
    ///     Raised once per partition after it has run. Payload:
    ///     <see cref="PartitionExecutedEvent" />.
    /// </summary>
    public const string PartitionExecuted = "EFBulk.PartitionExecuted";

    /// <summary>
    ///     Raised when an explicit bulk call could not be accelerated and ran through EF Core
    ///     instead. Payload: <see cref="ExplicitFallbackEvent" />.
    /// </summary>
    public const string ExplicitFallback = "EFBulk.ExplicitFallback";

    internal static readonly DiagnosticListener Listener = new(ListenerName);

    internal static void ReportExplicitFallback(Type entityType, int rowCount, string? reason)
    {
        if (Listener.IsEnabled(ExplicitFallback))
        {
            Listener.Write(ExplicitFallback, new ExplicitFallbackEvent(entityType, rowCount, reason));
        }
    }

    internal static void ReportPartitionsPlanned(IReadOnlyList<BulkPartition> partitions)
    {
        if (Listener.IsEnabled(PartitionsPlanned))
        {
            Listener.Write(PartitionsPlanned, new PartitionsPlannedEvent(partitions));
        }
    }

    internal static void ReportPartitionExecuted(
        BulkPartition partition,
        bool accelerated,
        string? fallbackReason,
        TimeSpan duration)
    {
        if (Listener.IsEnabled(PartitionExecuted))
        {
            Listener.Write(
                PartitionExecuted,
                new PartitionExecutedEvent(partition, accelerated, fallbackReason, duration));
        }
    }
}

/// <summary>Payload of <see cref="BulkDiagnostics.ExplicitFallback" />.</summary>
/// <param name="EntityType">The entity type being written.</param>
/// <param name="RowCount">How many rows were affected.</param>
/// <param name="Reason">Why the provider could not accelerate the call.</param>
public sealed record ExplicitFallbackEvent(Type EntityType, int RowCount, string? Reason);

/// <summary>Payload of <see cref="BulkDiagnostics.PartitionsPlanned" />.</summary>
/// <param name="Partitions">The partitions a single batch was grouped into.</param>
public sealed record PartitionsPlannedEvent(IReadOnlyList<BulkPartition> Partitions);

/// <summary>Payload of <see cref="BulkDiagnostics.PartitionExecuted" />.</summary>
/// <param name="Partition">The partition that ran.</param>
/// <param name="Accelerated">
///     <see langword="true" /> if it ran as a native bulk operation, <see langword="false" /> if it
///     was replayed through EF Core.
/// </param>
/// <param name="FallbackReason">Why it fell back, when <paramref name="Accelerated" /> is false.</param>
/// <param name="Duration">
///     Wall-clock time spent writing this partition. Excludes everything EF Core does before the
///     batch is handed over — change detection, command materialisation, dependency ordering —
///     which is what makes it useful for telling "the copy is slow" apart from "the copy is a small
///     part of the total".
/// </param>
public sealed record PartitionExecutedEvent(
    BulkPartition Partition,
    bool Accelerated,
    string? FallbackReason,
    TimeSpan Duration);
