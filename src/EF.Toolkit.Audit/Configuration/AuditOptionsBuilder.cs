using System.Text.Json;
using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Fluent surface behind <c>UseAuditing()</c>.
/// </summary>
/// <remarks>
///     Every method is <see langword="virtual" /> so a provider package can subclass this and add
///     knobs that only make sense for its engine, exactly as EF.Toolkit.Bulk's options builders are
///     extended. Settings that cannot be checked in isolation — a sink that cannot honour the
///     configured atomicity, a required actor with nothing to resolve one from — are checked once
///     the whole configuration is known, in <see cref="AuditOptionsExtension.Validate" />, so that
///     the order the calls were written in never changes the outcome.
/// </remarks>
public class AuditOptionsBuilder
{
    /// <summary>Initializes a new instance over <paramref name="options" />.</summary>
    /// <param name="options">The settings to start from.</param>
    public AuditOptionsBuilder(AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The settings built so far.</summary>
    public AuditOptions Options { get; protected set; }

    /// <summary>Puts the audit table in <paramref name="schema" />.</summary>
    /// <param name="schema">The schema, or <see langword="null" /> for the context's default.</param>
    public virtual AuditOptionsBuilder Schema(string? schema)
    {
        Options = Options with { Schema = schema };
        return this;
    }

    /// <summary>Names the audit table.</summary>
    /// <param name="name">The table name.</param>
    public virtual AuditOptionsBuilder TableName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Options = Options with { TableName = name };
        return this;
    }

    /// <summary>
    ///     Says another <c>DbContext</c> owns the audit table's migrations, so this one emits no DDL
    ///     for it.
    /// </summary>
    /// <remarks>
    ///     Set this on every context but one when several share a database and one audit table.
    ///     Without it each context's migrations would try to create the same table, which is
    ///     discovered at the worst possible moment.
    /// </remarks>
    public virtual AuditOptionsBuilder SharedAuditTables()
    {
        Options = Options with { SharedAuditTables = true };
        return this;
    }

    /// <summary>Audits every mapped entity type unless it opts out.</summary>
    /// <remarks>
    ///     Inverts the model-wide default. Types still opt out with <c>IsNotAudited()</c> or
    ///     <c>[NotAudited]</c>, and keyless types are skipped — there is no key to record.
    /// </remarks>
    public virtual AuditOptionsBuilder AuditAllEntities()
    {
        Options = Options with { AuditAllEntities = true };
        return this;
    }

    /// <summary>Restricts which operations produce an entry, for types that do not say themselves.</summary>
    /// <param name="operations">The operations to audit.</param>
    public virtual AuditOptionsBuilder Operations(AuditOperations operations)
    {
        if (operations == AuditOperations.None)
        {
            throw new AuditNotSupportedException(
                "Operations(AuditOperations.None) audits nothing at all. Leave UseAuditing() off "
                + "instead, so that nothing is registered and nothing pays for it.");
        }

        Options = Options with { Operations = operations };
        return this;
    }

    /// <summary>Chooses what payload value keys are named after.</summary>
    /// <param name="names">Property names, or column names.</param>
    public virtual AuditOptionsBuilder PayloadNames(AuditPayloadNames names)
    {
        Options = Options with { PayloadNames = names };
        return this;
    }

    /// <summary>Chooses what the entry's entity-type column holds.</summary>
    /// <param name="names">The short name, the full name, or the table name.</param>
    public virtual AuditOptionsBuilder StoreEntityTypeAs(AuditEntityTypeNames names)
    {
        Options = Options with { StoreEntityTypeAs = names };
        return this;
    }

    /// <summary>Truncates serialized values longer than <paramref name="length" />.</summary>
    /// <param name="length">The limit in characters. Zero disables truncation.</param>
    public virtual AuditOptionsBuilder MaxValueLength(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Options = Options with { MaxValueLength = length };
        return this;
    }

    /// <summary>Sets what a masked value is recorded as.</summary>
    /// <param name="token">The replacement text.</param>
    public virtual AuditOptionsBuilder MaskWith(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        Options = Options with { MaskToken = token };
        return this;
    }

    /// <summary>Masks every property matching <paramref name="predicate" />, across the model.</summary>
    /// <param name="predicate">Chooses properties by name, type, or anything else on the metadata.</param>
    public virtual AuditOptionsBuilder MaskProperties(Func<IProperty, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        Options = Options with { MaskPredicate = predicate };
        return this;
    }

    /// <summary>Chooses how entries are kept atomic with the change they describe.</summary>
    /// <param name="atomicity">The atomicity guarantee.</param>
    public virtual AuditOptionsBuilder Atomicity(AuditAtomicity atomicity)
    {
        Options = Options with { Atomicity = atomicity };
        return this;
    }

    /// <summary>Chooses what happens when entries cannot be written.</summary>
    /// <param name="failure">Throw, or report and continue.</param>
    public virtual AuditOptionsBuilder OnAuditFailure(AuditFailure failure)
    {
        Options = Options with { OnAuditFailure = failure };
        return this;
    }

    /// <summary>Sets the entry count from which a registered batch writer is used.</summary>
    /// <param name="entries">The threshold.</param>
    public virtual AuditOptionsBuilder BatchThreshold(int entries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entries);

        Options = Options with { BatchThreshold = entries };
        return this;
    }

    /// <summary>Chooses whether bulk updates and deletes capture the rows as they were.</summary>
    /// <param name="capture">
    ///     <see langword="false" /> to record new values only, at the cost of a weaker trail for
    ///     rows changed through the explicit bulk API than for rows changed through
    ///     <c>SaveChanges</c>.
    /// </param>
    public virtual AuditOptionsBuilder CaptureBeforeImages(bool capture = true)
    {
        Options = Options with { CaptureBeforeImages = capture };
        return this;
    }

    /// <summary>Chooses which indexes the audit table is created with.</summary>
    /// <param name="indexes">The indexes.</param>
    public virtual AuditOptionsBuilder Indexes(AuditIndexes indexes)
    {
        Options = Options with { Indexes = indexes };
        return this;
    }

    /// <summary>Generates entry keys with <paramref name="factory" />.</summary>
    /// <typeparam name="TKey">The key type, which becomes the audit table's key column type.</typeparam>
    /// <param name="factory">
    ///     Produces the next key. Receives the application's service provider, so an existing
    ///     identifier generator can be used directly with no adapter.
    /// </param>
    /// <example>
    ///     <code>
    ///     a.Ids&lt;string&gt;(sp => sp.GetRequiredService&lt;IUserFriendlyIdGenerator&gt;().Generate("aud"))
    ///     </code>
    /// </example>
    public virtual AuditOptionsBuilder Ids<TKey>(Func<IServiceProvider, TKey> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        Options = Options with
        {
            KeyType = typeof(TKey),
            IdFactory = factory,
            IdProviderType = null,
            StoreGeneratedIds = false,
        };

        return this;
    }

    /// <summary>Generates entry keys with an application-registered provider.</summary>
    /// <typeparam name="TProvider">The provider, resolved from application services.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    public virtual AuditOptionsBuilder IdsFrom<TProvider, TKey>()
        where TProvider : IAuditEntryIdProvider<TKey>
    {
        Options = Options with
        {
            KeyType = typeof(TKey),
            IdFactory = null,
            IdProviderType = typeof(TProvider),
            StoreGeneratedIds = false,
        };

        return this;
    }

    /// <summary>Lets the database generate entry keys.</summary>
    /// <typeparam name="TKey">The key type — normally <see cref="long" />.</typeparam>
    /// <remarks>
    ///     Costs a read-back on every audit insert, which is what a client-generated key exists to
    ///     avoid. Worth it only where an existing convention demands a database-generated identity.
    /// </remarks>
    public virtual AuditOptionsBuilder StoreGeneratedIds<TKey>()
    {
        Options = Options with
        {
            KeyType = typeof(TKey),
            IdFactory = null,
            IdProviderType = null,
            StoreGeneratedIds = true,
        };

        return this;
    }

    /// <summary>Lets the database generate <see cref="long" /> entry keys.</summary>
    public virtual AuditOptionsBuilder BigIntKeys()
        => StoreGeneratedIds<long>();

    /// <summary>Uses a fixed or computed actor.</summary>
    /// <param name="actor">Produces the actor. Called once per save.</param>
    public virtual AuditOptionsBuilder Actor(Func<AuditActor> actor)
    {
        ArgumentNullException.ThrowIfNull(actor);

        Options = Options with
        {
            ActorResolver = (_, _) => ValueTask.FromResult(actor()),
        };

        return this;
    }

    /// <summary>Reads the actor off an application-registered service.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <param name="read">Turns that service into an actor.</param>
    /// <example>
    ///     <code>a.ActorFrom&lt;ICurrentUser&gt;(u => new AuditActor(u.Id, u.Name))</code>
    /// </example>
    public virtual AuditOptionsBuilder ActorFrom<TService>(Func<TService, AuditActor> read)
        where TService : notnull
    {
        ArgumentNullException.ThrowIfNull(read);

        Options = Options with
        {
            ActorResolver = (services, _) => ValueTask.FromResult(
                read(AuditServiceResolver.Required<TService>(services, "ActorFrom<...>()"))),
        };

        return this;
    }

    /// <summary>Resolves the actor through an application-registered provider.</summary>
    /// <typeparam name="TProvider">The provider, resolved from application services.</typeparam>
    public virtual AuditOptionsBuilder ActorFrom<TProvider>()
        where TProvider : IAuditActorProvider
    {
        Options = Options with
        {
            ActorResolver = static (services, cancellationToken) =>
                AuditServiceResolver.Required<TProvider>(services, "ActorFrom<...>()")
                    .GetActorAsync(cancellationToken),
        };

        return this;
    }

    /// <summary>Refuses to write an audit entry whose actor could not be determined.</summary>
    public virtual AuditOptionsBuilder RequireActor()
    {
        Options = Options with { RequireActor = true };
        return this;
    }

    /// <summary>Configures where an entry's tenant comes from.</summary>
    /// <param name="configure">The tenant configuration.</param>
    public virtual AuditOptionsBuilder MultiTenant(Action<AuditMultiTenantBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new AuditMultiTenantBuilder(Options);
        configure(builder);
        Options = builder.Options;

        return this;
    }

    /// <summary>Writes entries through the context that produced the change. The default.</summary>
    public virtual AuditOptionsBuilder WriteToSameContext()
    {
        Options = Options with { SinkType = null, ExternalContextType = null };
        return this;
    }

    /// <summary>Writes entries through a dedicated audit context.</summary>
    /// <typeparam name="TContext">The audit context, resolved from application services.</typeparam>
    /// <remarks>
    ///     Not atomic with the change, so it needs <c>Atomicity(AuditAtomicity.BestEffort)</c> —
    ///     stated explicitly rather than assumed, because the guarantee being given up is the whole
    ///     reason the default sink exists.
    /// </remarks>
    public virtual AuditOptionsBuilder WriteToContext<TContext>()
        where TContext : DbContext
    {
        Options = Options with { SinkType = null, ExternalContextType = typeof(TContext) };
        return this;
    }

    /// <summary>Writes entries through a custom sink.</summary>
    /// <typeparam name="TSink">The sink, resolved from application services.</typeparam>
    /// <remarks>
    ///     Compatible with either atomicity, because only the sink knows where it writes. Under
    ///     <see cref="AuditAtomicity.SameTransaction" /> it is handed the change's transaction on
    ///     <see cref="AuditWriteContext" /> and is expected to use it; a sink that writes somewhere
    ///     that transaction cannot reach should say <see cref="AuditAtomicity.BestEffort" />.
    /// </remarks>
    public virtual AuditOptionsBuilder WriteTo<TSink>()
        where TSink : class, IAuditSink
    {
        Options = Options with { SinkType = typeof(TSink), ExternalContextType = null };
        return this;
    }

    /// <summary>Sets the serialization options used for payload values.</summary>
    /// <param name="json">The options.</param>
    public virtual AuditOptionsBuilder Json(JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Options = Options with { Json = json };
        return this;
    }

    /// <summary>Sets where <c>OccurredAt</c> is read from.</summary>
    /// <param name="timeProvider">The clock.</param>
    public virtual AuditOptionsBuilder UseTimeProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        Options = Options with { TimeProvider = timeProvider };
        return this;
    }
}
