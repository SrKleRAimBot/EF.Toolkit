using EFToolkit.Audit.Api;
using EFToolkit.Audit.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace EFToolkit.Audit.Tests.Infrastructure;

/// <summary>
///     Builds finalized EF models without a database.
/// </summary>
/// <remarks>
///     Everything about registration, exclusion, masking and payload shape is decided from model
///     metadata, and metadata does not need a server to exist. Building the model in-process keeps
///     those tests measured in milliseconds and independent of Docker, leaving the integration suite
///     to assert the things only a real engine can answer.
/// </remarks>
public static class TestModel
{
    /// <summary>Builds a context whose model is finalized but which never connects.</summary>
    /// <param name="configure">Auditing settings.</param>
    /// <param name="onModelCreating">Model configuration.</param>
    /// <param name="applicationServices">
    ///     Stands in for the container an application would have registered the context with. The
    ///     actor, tenant and identifier providers are resolved from it, not from EF's internal
    ///     provider, so the tests that use them need one.
    /// </param>
    public static AuditTestContext Context(
        Action<AuditOptionsBuilder>? configure = null,
        Action<ModelBuilder>? onModelCreating = null,
        IServiceProvider? applicationServices = null)
    {
        var builder = new DbContextOptionsBuilder<AuditTestContext>()
            .UseNpgsql("Host=localhost;Database=none")
            .UseApplicationServiceProvider(applicationServices)
            .UseNpgsqlAuditing(a =>
            {
                a.UseTimeProvider(FixedTimeProvider.Default);
                configure?.Invoke(a);
            })

            // Every context here shares one CLR type and supplies its model through a delegate, so
            // EF's default cache key — the context type — would hand the second test the first
            // test's model. Nothing about that is a problem in an application, where one context
            // type means one model; it is purely an artefact of configuring the model per test.
            .ReplaceService<IModelCacheKeyFactory, PerInstanceModelCacheKeyFactory>();

        return new AuditTestContext(builder.Options, onModelCreating);
    }

    /// <summary>The settings a context ended up with.</summary>
    public static AuditOptions Options(this AuditTestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.GetService<AuditOptions>();
    }
}

/// <summary>A context whose model is supplied per test.</summary>
public class AuditTestContext(
    DbContextOptions<AuditTestContext> options,
    Action<ModelBuilder>? onModelCreating = null) : DbContext(options)
{
    internal object ModelCacheKey { get; } = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => onModelCreating?.Invoke(modelBuilder);
}

/// <summary>Gives every context instance a model of its own.</summary>
public sealed class PerInstanceModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => (((AuditTestContext)context).ModelCacheKey, designTime);
}

/// <summary>A clock that does not move, so a captured timestamp is assertable.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    /// <summary>The instant every entry in the suite is stamped with.</summary>
    public static DateTimeOffset Instant { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A provider pinned to <see cref="Instant" />.</summary>
    public static FixedTimeProvider Default { get; } = new(Instant);

    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>An identifier generator that hands out predictable values.</summary>
public sealed class SequentialIdProvider : IAuditEntryIdProvider<string>
{
    private int _next;

    /// <summary>How many times the batch overload was used.</summary>
    public int BatchCalls { get; private set; }

    public string Generate() => $"aud_{++_next:D4}";

    public void Generate(Span<string> destination)
    {
        BatchCalls++;

        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = Generate();
        }
    }
}
