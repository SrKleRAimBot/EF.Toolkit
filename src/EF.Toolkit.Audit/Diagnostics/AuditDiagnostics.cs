using System.Diagnostics;
using EFToolkit.Audit.Api;

namespace EFToolkit.Audit.Diagnostics;

/// <summary>
///     Diagnostic events emitted by EF.Toolkit.Audit.
/// </summary>
/// <remarks>
///     <para>
///         Auditing is the kind of feature that is assumed to be working until somebody looks for
///         an entry that is not there. These exist so that "is it capturing?" and "is it writing?"
///         are answerable at runtime rather than by inspecting the table and guessing.
///     </para>
///     <para>
///         Subscribe with <see cref="DiagnosticListener.AllListeners" />, matching
///         <see cref="ListenerName" />. Every payload is a strongly-typed record, and events are
///         only constructed when something is listening.
///     </para>
/// </remarks>
public static class AuditDiagnostics
{
    /// <summary>Name of the <see cref="DiagnosticListener" /> EF.Toolkit.Audit publishes to.</summary>
    public const string ListenerName = "EF.Toolkit.Audit";

    /// <summary>
    ///     Raised once per unit of work after the change set has been turned into entries. Payload:
    ///     <see cref="EntriesCapturedEvent" />.
    /// </summary>
    public const string EntriesCaptured = "EF.Toolkit.Audit.EntriesCaptured";

    /// <summary>
    ///     Raised once per unit of work after the entries have reached the sink. Payload:
    ///     <see cref="EntriesWrittenEvent" />.
    /// </summary>
    public const string EntriesWritten = "EF.Toolkit.Audit.EntriesWritten";

    /// <summary>
    ///     Raised when a change was not audited that a reader might expect to have been. Payload:
    ///     <see cref="AuditSkippedEvent" />.
    /// </summary>
    /// <remarks>
    ///     Chiefly an update whose properties all compared equal, which produces no entry because
    ///     nothing changed. That is correct and it is also the single most likely reason for someone
    ///     to conclude auditing is broken, so it is reported rather than silent.
    /// </remarks>
    public const string AuditSkipped = "EF.Toolkit.Audit.AuditSkipped";

    /// <summary>
    ///     Raised when the sink failed. Payload: <see cref="SinkFailedEvent" />.
    /// </summary>
    /// <remarks>
    ///     Raised under both failure policies — <c>Throw</c> reports and rethrows — so a listener
    ///     sees every failure regardless of what the application chose to do about it.
    /// </remarks>
    public const string SinkFailed = "EF.Toolkit.Audit.SinkFailed";

    internal static readonly DiagnosticListener Listener = new(ListenerName);

    internal static void ReportEntriesCaptured(string source, int sourceCount, int entryCount)
    {
        if (Listener.IsEnabled(EntriesCaptured))
        {
            Listener.Write(EntriesCaptured, new EntriesCapturedEvent(source, sourceCount, entryCount));
        }
    }

    internal static void ReportEntriesWritten(string source, int entryCount, bool batched)
    {
        if (Listener.IsEnabled(EntriesWritten))
        {
            Listener.Write(EntriesWritten, new EntriesWrittenEvent(source, entryCount, batched));
        }
    }

    internal static void ReportAuditSkipped(Type entityType, AuditOperation operation, string reason)
    {
        if (Listener.IsEnabled(AuditSkipped))
        {
            Listener.Write(AuditSkipped, new AuditSkippedEvent(entityType, operation, reason));
        }
    }

    internal static void ReportSinkFailed(string source, int entryCount, Exception exception)
    {
        if (Listener.IsEnabled(SinkFailed))
        {
            Listener.Write(SinkFailed, new SinkFailedEvent(source, entryCount, exception));
        }
    }
}

/// <summary>Payload of <see cref="AuditDiagnostics.EntriesCaptured" />.</summary>
/// <param name="Source">Which write path was audited.</param>
/// <param name="SourceCount">How many entity-type-and-operation groups were examined.</param>
/// <param name="EntryCount">How many entries came out of them.</param>
public sealed record EntriesCapturedEvent(string Source, int SourceCount, int EntryCount);

/// <summary>Payload of <see cref="AuditDiagnostics.EntriesWritten" />.</summary>
/// <param name="Source">Which write path was audited.</param>
/// <param name="EntryCount">How many entries were written.</param>
/// <param name="Batched">
///     <see langword="true" /> when a registered <see cref="IAuditBatchWriter" /> wrote them,
///     <see langword="false" /> when they went through <c>SaveChanges</c>. The answer to "did
///     installing EF.Toolkit.Audit.Bulk actually take effect".
/// </param>
public sealed record EntriesWrittenEvent(string Source, int EntryCount, bool Batched);

/// <summary>Payload of <see cref="AuditDiagnostics.AuditSkipped" />.</summary>
/// <param name="EntityType">The entity type that was not audited.</param>
/// <param name="Operation">What was being done to it.</param>
/// <param name="Reason">Why no entry was produced.</param>
public sealed record AuditSkippedEvent(Type EntityType, AuditOperation Operation, string Reason);

/// <summary>Payload of <see cref="AuditDiagnostics.SinkFailed" />.</summary>
/// <param name="Source">Which write path was being audited.</param>
/// <param name="EntryCount">How many entries were lost, or would have been.</param>
/// <param name="Exception">What went wrong.</param>
public sealed record SinkFailedEvent(string Source, int EntryCount, Exception Exception);
