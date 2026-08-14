namespace EFToolkit.Audit.Api;

/// <summary>
///     Ambient context for everything an audit entry needs that the change itself cannot supply —
///     who is acting, why, and what correlates this change with others.
/// </summary>
/// <remarks>
///     <para>
///         The actor is usually resolved from application services, but that only works where there
///         is a request to resolve it from. A background job, a migration, a console tool and the
///         explicit bulk API all have real actors and no ambient request, and this is how they say
///         so. A scope also wins over every configured provider, so a request-scoped default can be
///         overridden for one operation without reconfiguring anything.
///     </para>
///     <para>
///         Scopes nest. An inner scope starts from the values of the one it is nested in, overrides
///         whatever it was given, and merges its metadata over the outer scope's — so a nested scope
///         adding <c>batch</c> keeps the outer scope's <c>reason</c>.
///     </para>
/// </remarks>
/// <example>
///     <code>
///     using (AuditScope.Begin(actor: "reprice-job", reason: "nightly reprice")
///                      .With("batch", batchNumber))
///     {
///         await context.BulkUpdateAsync(prices);
///     }
///     </code>
/// </example>
public sealed class AuditScope : IDisposable
{
    // AsyncLocal rather than ThreadStatic: the flow crosses awaits, and every capture point in this
    // library is async.
    private static readonly AsyncLocal<AuditScope?> Ambient = new();

    private readonly AuditScope? _parent;
    private Dictionary<string, object?>? _metadata;
    private bool _disposed;

    private AuditScope(AuditScope? parent)
        => _parent = parent;

    /// <summary>The innermost scope currently in effect, or <see langword="null" />.</summary>
    public static AuditScope? Current => Ambient.Value;

    /// <summary>The actor, or <see langword="null" /> to leave it to the configured provider.</summary>
    public AuditActor? Actor { get; private set; }

    /// <summary>The tenant, or <see langword="null" /> to leave it to the configured resolution.</summary>
    public string? TenantId { get; private set; }

    /// <summary>Why the change is being made, recorded in the entry's payload.</summary>
    public string? Reason { get; private set; }

    /// <summary>Ties every entry written inside this scope to one another.</summary>
    public Guid? CorrelationId { get; private set; }

    /// <summary>Free-form values merged into the <c>meta</c> object of every entry's payload.</summary>
    public IReadOnlyDictionary<string, object?> Metadata
        => _metadata ?? (IReadOnlyDictionary<string, object?>)EmptyMetadata;

    private static Dictionary<string, object?> EmptyMetadata { get; } = [];

    /// <summary>Begins a scope, inheriting anything not given from the scope it nests inside.</summary>
    /// <param name="actor">Who is acting.</param>
    /// <param name="reason">Why.</param>
    /// <param name="correlationId">
    ///     Ties the entries written inside this scope together. When omitted, a nested scope keeps
    ///     the outer correlation id and an outermost scope gets a fresh one, so entries are always
    ///     correlatable without the caller having to think about it.
    /// </param>
    /// <param name="tenantId">The tenant, when it cannot be read from the entities themselves.</param>
    public static AuditScope Begin(
        AuditActor? actor = null,
        string? reason = null,
        Guid? correlationId = null,
        string? tenantId = null)
    {
        var parent = Ambient.Value;

        var scope = new AuditScope(parent)
        {
            Actor = actor ?? parent?.Actor,
            Reason = reason ?? parent?.Reason,
            CorrelationId = correlationId ?? parent?.CorrelationId ?? Guid.CreateVersion7(),
            TenantId = tenantId ?? parent?.TenantId,
        };

        if (parent?._metadata is { Count: > 0 } inherited)
        {
            scope._metadata = new Dictionary<string, object?>(inherited, StringComparer.Ordinal);
        }

        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Begins a scope for a named actor.</summary>
    /// <param name="actor">The actor's identifier.</param>
    /// <param name="reason">Why the change is being made.</param>
    /// <param name="correlationId">Ties the entries written inside this scope together.</param>
    /// <param name="tenantId">The tenant, when it cannot be read from the entities themselves.</param>
    public static AuditScope Begin(
        string actor,
        string? reason = null,
        Guid? correlationId = null,
        string? tenantId = null)
        => Begin(new AuditActor(actor), reason, correlationId, tenantId);

    /// <summary>Adds a value to this scope's metadata.</summary>
    /// <param name="key">The name it appears under in the payload's <c>meta</c> object.</param>
    /// <param name="value">The value. Serialized with the same options as the rest of the payload.</param>
    public AuditScope With(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        (_metadata ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = value;
        return this;
    }

    /// <summary>Restores the scope this one nests inside.</summary>
    /// <remarks>
    ///     Restoring only when this scope is the current one keeps an out-of-order disposal — a
    ///     scope captured and disposed on another flow, say — from resurrecting an outer scope that
    ///     has already ended.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (ReferenceEquals(Ambient.Value, this))
        {
            Ambient.Value = _parent;
        }
    }
}
