using System.Diagnostics;
using EFBulk.Diagnostics;
using EFBulk.Planning;

namespace EFBulk.Equivalence.Infrastructure;

/// <summary>
///     Subscribes to <see cref="BulkDiagnostics" /> and records what EF.Bulk actually did.
/// </summary>
/// <remarks>
///     Without this, the suite could only observe that results match — which they trivially would
///     if the partitioner were a no-op returning one partition per batch. These recordings let a
///     test assert that regrouping genuinely happened.
/// </remarks>
public sealed class PartitionRecorder : IDisposable, IObserver<DiagnosticListener>
{
    /// <summary>
    ///     Identifies the async flow a recorder was created on, so it records only its own work.
    /// </summary>
    /// <remarks>
    ///     <see cref="BulkDiagnostics" /> publishes to a process-wide listener, and the engine
    ///     suites run in parallel — so without this a recorder in one collection captures partitions
    ///     from another and the assertions become a race. An <see cref="AsyncLocal{T}" /> set before
    ///     the work starts flows through every await into the library, and diagnostic events are
    ///     raised synchronously on that same flow, so the value is still visible when the event
    ///     arrives.
    ///     <para>
    ///         The value is pushed and popped rather than set and cleared, so nesting two recorders
    ///         on one flow is safe: the inner one takes over while it lives and the outer one
    ///         resumes when it is disposed. Clearing unconditionally would leave the outer recorder
    ///         silently capturing nothing for the rest of its life.
    ///     </para>
    /// </remarks>
    private static readonly AsyncLocal<Guid?> Scope = new();

    private readonly Guid _scopeId = Guid.NewGuid();
    private readonly Guid? _enclosingScope;

    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;
    private readonly Lock _gate = new();

    private readonly List<IReadOnlyList<BulkPartition>> _batches = [];
    private readonly List<PartitionExecutedEvent> _executions = [];

    public PartitionRecorder()
    {
        _enclosingScope = Scope.Value;
        Scope.Value = _scopeId;
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>The partition sets planned, one entry per batch.</summary>
    public IReadOnlyList<IReadOnlyList<BulkPartition>> Batches
    {
        get { lock (_gate) { return [.. _batches]; } }
    }

    /// <summary>Every partition execution, in order.</summary>
    public IReadOnlyList<PartitionExecutedEvent> Executions
    {
        get { lock (_gate) { return [.. _executions]; } }
    }

    /// <summary>Batches that were split into more than one partition.</summary>
    public IEnumerable<IReadOnlyList<BulkPartition>> MultiPartitionBatches
        => Batches.Where(b => b.Count > 1);

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == BulkDiagnostics.ListenerName)
        {
            _subscriptions.Add(listener.Subscribe(new EventObserver(this)));
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }

    private sealed class EventObserver(PartitionRecorder owner) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Scope.Value != owner._scopeId)
            {
                return;
            }

            lock (owner._gate)
            {
                switch (value.Value)
                {
                    case PartitionsPlannedEvent planned:
                        owner._batches.Add(planned.Partitions);
                        break;
                    case PartitionExecutedEvent executed:
                        owner._executions.Add(executed);
                        break;
                }
            }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    public void Dispose()
    {
        Scope.Value = _enclosingScope;

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _allListeners.Dispose();
    }
}
