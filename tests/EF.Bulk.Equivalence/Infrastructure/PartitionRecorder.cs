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
    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;
    private readonly Lock _gate = new();

    private readonly List<IReadOnlyList<BulkPartition>> _batches = [];
    private readonly List<PartitionExecutedEvent> _executions = [];

    public PartitionRecorder()
        => _allListeners = DiagnosticListener.AllListeners.Subscribe(this);

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
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _allListeners.Dispose();
    }
}
