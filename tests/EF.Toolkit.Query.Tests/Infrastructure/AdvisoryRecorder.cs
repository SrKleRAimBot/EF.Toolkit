using System.Diagnostics;
using EFToolkit.Query.Configuration;
using EFToolkit.Query.Diagnostics;

namespace EFToolkit.Query.Tests.Infrastructure;

/// <summary>Captures the advisories published while it is alive.</summary>
/// <remarks>
///     <see cref="QueryDiagnostics" /> publishes to a process-wide listener and xunit runs test
///     classes in parallel, so without a scope a recorder in one test captures another test's
///     advisories and the assertions become a race. The scope id is pushed and popped rather than set
///     and cleared, so nesting two recorders on one flow is safe.
/// </remarks>
public sealed class AdvisoryRecorder : IDisposable, IObserver<DiagnosticListener>
{
    private static readonly AsyncLocal<Guid?> Scope = new();

    private readonly List<IDisposable> _subscriptions = [];
    private readonly IDisposable _allListeners;
    private readonly Guid _scopeId = Guid.NewGuid();
    private readonly Guid? _enclosingScope;
    private readonly Lock _gate = new();

    public AdvisoryRecorder()
    {
        _enclosingScope = Scope.Value;
        Scope.Value = _scopeId;
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public List<QueryAdvisory> Advisories { get; } = [];

    public IReadOnlyList<QueryChecks> Checks
    {
        get
        {
            lock (_gate)
            {
                return Advisories.Select(static a => a.Check).ToArray();
            }
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == QueryDiagnostics.ListenerName)
        {
            _subscriptions.Add(value.Subscribe(new EventObserver(this)));
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted()
    {
    }

    void IObserver<DiagnosticListener>.OnError(Exception error)
    {
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

    private void Record(QueryAdvisory advisory)
    {
        if (Scope.Value != _scopeId)
        {
            return;
        }

        lock (_gate)
        {
            Advisories.Add(advisory);
        }
    }

    private sealed class EventObserver(AdvisoryRecorder owner)
        : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Value is QueryAdvisoryEvent published)
            {
                owner.Record(published.Advisory);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
