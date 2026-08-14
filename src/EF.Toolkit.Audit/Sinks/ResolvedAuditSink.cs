using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Audit.Sinks;

/// <summary>
///     Forwards to an application-registered <see cref="IAuditSink" />.
/// </summary>
/// <remarks>
///     A custom sink almost always depends on something the application registered — a message bus,
///     an outbox, a client — none of which lives in EF Core's internal service provider. Resolving
///     it from the application's provider instead is what lets a sink be written the way any other
///     application service is.
/// </remarks>
internal sealed class ResolvedAuditSink(AuditOptions options, IDbContextOptions contextOptions)
    : IAuditSink
{
    private IAuditSink? _inner;

    /// <inheritdoc />
    public Task WriteAsync(
        IReadOnlyList<AuditEntry> entries,
        AuditWriteContext context,
        CancellationToken cancellationToken)
        => (_inner ??= Resolve()).WriteAsync(entries, context, cancellationToken);

    private IAuditSink Resolve()
    {
        var services = contextOptions
            .FindExtension<CoreOptionsExtension>()?.ApplicationServiceProvider;

        return AuditServiceResolver.Required<IAuditSink>(
            new SinkOnlyServiceProvider(services, options.SinkType!),
            $"WriteTo<{options.SinkType!.Name}>()");
    }

    /// <summary>
    ///     Presents the configured sink type as <see cref="IAuditSink" />.
    /// </summary>
    /// <remarks>
    ///     The application registers its sink as its own type, not as the interface, so asking for
    ///     the interface directly would miss it. This translates the one question
    ///     <see cref="AuditServiceResolver" /> asks into the one the application answered, so that a
    ///     missing registration still produces that class's message rather than a null reference.
    /// </remarks>
    private sealed class SinkOnlyServiceProvider(IServiceProvider? inner, Type sinkType)
        : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType == typeof(IAuditSink) ? inner?.GetService(sinkType) : null;
    }
}
