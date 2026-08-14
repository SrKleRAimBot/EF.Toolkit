using System.Text.Json;
using EFToolkit.Audit.Api;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EFToolkit.Audit.Configuration;

/// <summary>
///     Context-wide auditing settings, established by <c>UseAuditing()</c>.
///     Immutable; <see cref="AuditOptionsBuilder" /> produces instances via <c>with</c>.
/// </summary>
/// <remarks>
///     A <see langword="record" /> on purpose. EF caches one internal service provider per distinct
///     options hash, and these are registered as a singleton there, so every setting has to
///     contribute to equality or two differently-configured contexts would share the first one's
///     configuration. Value equality over every field is exactly that guarantee, for free and
///     without anyone having to remember to extend a hand-written hash.
/// </remarks>
public sealed record AuditOptions
{
    /// <summary>The default value of <see cref="Schema" />.</summary>
    public const string DefaultSchema = "audit";

    /// <summary>The default value of <see cref="TableName" />.</summary>
    public const string DefaultTableName = "AuditEntries";

    /// <summary>The default value of <see cref="MaxValueLength" />.</summary>
    public const int DefaultMaxValueLength = 4096;

    /// <summary>The default value of <see cref="BatchThreshold" />.</summary>
    public const int DefaultBatchThreshold = 100;

    /// <summary>The default value of <see cref="MaskToken" />.</summary>
    public const string DefaultMaskToken = "***";

    /// <summary>The marker added to a payload whose values were truncated.</summary>
    public const string TruncatedMarker = "__truncated";

    // A static delegate rather than one built per call, so that two contexts configured identically
    // hash identically and share EF's internal service provider as they should.
    private static readonly Func<IServiceProvider, Guid> DefaultIds = static _ => Guid.CreateVersion7();

    /// <summary>Settings used when <c>UseAuditing()</c> is called with no configuration.</summary>
    public static AuditOptions Default { get; } = new();

    /// <summary>
    ///     Schema the audit table lives in, or <see langword="null" /> for the context's default.
    /// </summary>
    /// <remarks>
    ///     A schema of its own by default. An audit table is not application data, it is written by
    ///     something the application does not call directly, and it grows differently from
    ///     everything around it — all of which are easier to act on when it is not sitting among
    ///     the tables it describes.
    /// </remarks>
    public string? Schema { get; init; } = DefaultSchema;

    /// <summary>Name of the audit table.</summary>
    public string TableName { get; init; } = DefaultTableName;

    /// <summary>
    ///     Whether the audit table is owned by a different <c>DbContext</c>'s migrations.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see langword="false" /> by default: the table is part of this context's model and
    ///         its migrations, which is what a single-context application wants and needs no call.
    ///     </para>
    ///     <para>
    ///         Set it on every context but one when several contexts share a database and write to
    ///         one audit table. The entity type is then marked <c>ExcludeFromMigrations</c>, so only
    ///         the owning context emits DDL for it and <c>dotnet ef migrations add</c> from the
    ///         others does not try to create a table that already exists.
    ///     </para>
    /// </remarks>
    public bool SharedAuditTables { get; init; }

    /// <summary>
    ///     Whether every mapped entity type is audited unless it opts out.
    /// </summary>
    /// <remarks>
    ///     <see langword="false" /> by default, so a type is audited only once somebody says it is.
    ///     Setting this inverts the model-wide default; <c>IsAudited()</c> and <c>IsNotAudited()</c>
    ///     remain meaningful either way.
    /// </remarks>
    public bool AuditAllEntities { get; init; }

    /// <summary>
    ///     Which operations produce an audit entry, for types that do not narrow it themselves.
    /// </summary>
    public AuditOperations Operations { get; init; } = AuditOperations.All;

    /// <summary>What the payload's value keys are named after. See <see cref="AuditPayloadNames" />.</summary>
    public AuditPayloadNames PayloadNames { get; init; } = AuditPayloadNames.Property;

    /// <summary>What the entry's entity-type column holds. See <see cref="AuditEntityTypeNames" />.</summary>
    public AuditEntityTypeNames StoreEntityTypeAs { get; init; } = AuditEntityTypeNames.Name;

    /// <summary>
    ///     Longest serialized value kept before it is truncated. Zero disables truncation.
    /// </summary>
    /// <remarks>
    ///     A guard against one <c>text</c> column turning the audit table into a second copy of the
    ///     database. A truncated entry is stamped with <see cref="TruncatedMarker" /> so a reader
    ///     can tell a shortened value from a short one.
    /// </remarks>
    public int MaxValueLength { get; init; } = DefaultMaxValueLength;

    /// <summary>What a masked value is recorded as, when no redactor was given.</summary>
    public string MaskToken { get; init; } = DefaultMaskToken;

    /// <summary>
    ///     Masks every property this matches, in addition to those masked individually.
    /// </summary>
    /// <remarks>
    ///     For a rule that should hold across the whole model — <c>p =&gt;
    ///     p.Name.EndsWith("Token")</c> — so a new secret-bearing property is masked from the moment
    ///     it is named, rather than when somebody remembers to configure it.
    /// </remarks>
    public Func<IProperty, bool>? MaskPredicate { get; init; }

    /// <summary>How audit entries are kept atomic with the change. See <see cref="AuditAtomicity" />.</summary>
    public AuditAtomicity Atomicity { get; init; } = AuditAtomicity.SameTransaction;

    /// <summary>What happens when entries cannot be written. See <see cref="AuditFailure" />.</summary>
    public AuditFailure OnAuditFailure { get; init; } = AuditFailure.Throw;

    /// <summary>
    ///     Number of entries from which the sink uses <see cref="IAuditBatchWriter" />, when one is
    ///     registered.
    /// </summary>
    /// <remarks>
    ///     Below this a plain insert wins: the fixed cost of a bulk copy is not worth paying for a
    ///     handful of rows, which is what an ordinary <c>SaveChanges</c> produces.
    /// </remarks>
    public int BatchThreshold { get; init; } = DefaultBatchThreshold;

    /// <summary>
    ///     Whether the explicit bulk API reads the rows it is about to change, so an update or
    ///     delete has old values as well as new. Defaults to <see langword="true" />.
    /// </summary>
    /// <remarks>
    ///     Free on SQL Server, which can return both halves of an <c>UPDATE</c> from one statement.
    ///     One extra indexed read on PostgreSQL, whose <c>RETURNING</c> cannot see the pre-update
    ///     row before version 18. Turning it off makes a bulk-updated row's trail weaker than a
    ///     <c>SaveChanges</c>-updated one, which is a difference worth choosing rather than
    ///     inheriting.
    /// </remarks>
    public bool CaptureBeforeImages { get; init; } = true;

    /// <summary>Which indexes the audit table is created with. See <see cref="AuditIndexes" />.</summary>
    public AuditIndexes Indexes { get; init; } = AuditIndexes.All;

    /// <summary>The audit entry's key type.</summary>
    public Type KeyType { get; init; } = typeof(Guid);

    /// <summary>
    ///     A <c>Func&lt;IServiceProvider, TKey&gt;</c> producing entry keys, or
    ///     <see langword="null" /> when <see cref="IdProviderType" /> or
    ///     <see cref="StoreGeneratedIds" /> supplies them instead.
    /// </summary>
    /// <remarks>
    ///     Held as a <see cref="Delegate" /> because the key type is configuration rather than a
    ///     type parameter here. It is closed over <see cref="KeyType" /> once, when the provider is
    ///     first needed, so no value is ever boxed on the per-entry path.
    /// </remarks>
    public Delegate? IdFactory { get; init; } = DefaultIds;

    /// <summary>
    ///     An <see cref="IAuditEntryIdProvider{TKey}" /> implementation resolved from application
    ///     services, or <see langword="null" />.
    /// </summary>
    public Type? IdProviderType { get; init; }

    /// <summary>
    ///     Whether the database generates entry keys.
    /// </summary>
    /// <remarks>
    ///     Off by default. A client-generated, time-ordered key indexes just as well and needs
    ///     nothing read back, which is what lets a large audit insert go down a bulk copy path with
    ///     no staging table and no sequence reservation. Turning this on gives that up in exchange
    ///     for a conventional <c>bigint</c> identity column.
    /// </remarks>
    public bool StoreGeneratedIds { get; init; }

    /// <summary>Resolves the actor from application services, or <see langword="null" />.</summary>
    public Func<IServiceProvider, CancellationToken, ValueTask<AuditActor>>? ActorResolver { get; init; }

    /// <summary>Whether an entry with no actor is a failure rather than a null column.</summary>
    public bool RequireActor { get; init; }

    /// <summary>
    ///     Name of the property the tenant is read from on each audited entity, or
    ///     <see langword="null" />.
    /// </summary>
    /// <remarks>
    ///     Shadow properties included, which is what makes this work with Finbuckle.MultiTenant
    ///     unchanged: its <c>IsMultiTenant()</c> adds a <c>TenantId</c> shadow property, and there
    ///     is nothing else to configure.
    /// </remarks>
    public string? TenantPropertyName { get; init; }

    /// <summary>Resolves the tenant from application services, or <see langword="null" />.</summary>
    public Func<IServiceProvider, CancellationToken, ValueTask<string?>>? TenantResolver { get; init; }

    /// <summary>Whether an entry with no tenant is a failure rather than a null column.</summary>
    public bool RequireTenant { get; init; }

    /// <summary>
    ///     The <see cref="IAuditSink" /> implementation, or <see langword="null" /> for the built-in
    ///     same-context sink.
    /// </summary>
    public Type? SinkType { get; init; }

    /// <summary>
    ///     A dedicated audit <c>DbContext</c> type, or <see langword="null" /> to write through the
    ///     context that produced the change.
    /// </summary>
    public Type? ExternalContextType { get; init; }

    /// <summary>Serialization settings for payload values, or <see langword="null" /> for the defaults.</summary>
    public JsonSerializerOptions? Json { get; init; }

    /// <summary>
    ///     Where <c>OccurredAt</c> comes from. Defaults to <see cref="TimeProvider.System" />.
    /// </summary>
    /// <remarks>
    ///     Configurable chiefly so a test can pin it. An audit entry's timestamp is part of what is
    ///     being asserted, and a clock read from static state cannot be.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Provider-supplied column types and index settings. See <see cref="AuditStoreTypes" />.</summary>
    public AuditStoreTypes StoreTypes { get; init; } = AuditStoreTypes.Default;

    /// <summary>Whether multi-tenancy has been configured in any form.</summary>
    public bool IsMultiTenant => TenantPropertyName is not null || TenantResolver is not null;
}
