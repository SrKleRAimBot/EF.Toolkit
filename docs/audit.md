# EF.Toolkit.Audit

Entity auditing for [EF Core](https://learn.microsoft.com/ef/core). Records who changed what and
when, with old and new values for every column that moved, into one audit table whose payload is
queryable JSON.

- **Fluent or attribute registration.** Fluent is preferred and wins; a genuine contradiction fails
  at startup rather than being resolved by a precedence rule.
- **Every property is captured.** Opting one out is always deliberate, so a column added later is
  audited from the moment it exists.
- **Queryable, not just readable.** `jsonb` behind a GIN index on PostgreSQL, so "which orders were
  ever shipped by this actor" is an indexed question.
- **Multi-tenant.** Reads the tenant off the audited entity, including a shadow property — which is
  the whole Finbuckle.MultiTenant integration.
- **Atomic.** Entries are written in the same transaction as the change, so one cannot exist without
  the other.
- **Bulk-aware.** With `EF.Toolkit.Audit.Bulk`, the explicit bulk API is audited too, and the entries
  themselves are written in bulk.

---

## Contents

- [Install](#install) · [Setup](#setup)
- [Registering entity types](#registering-entity-types) · [Registering properties](#registering-properties)
- [The actor](#the-actor) · [Ambient scopes](#ambient-scopes) · [Multi-tenancy](#multi-tenancy)
- [The audit table](#the-audit-table) · [The payload](#the-payload) · [Querying it](#querying-it)
- [The entry key](#the-entry-key) · [Where entries go](#where-entries-go)
- [Several DbContexts](#several-dbcontexts)
- [With EF.Toolkit.Bulk](#with-eftoolkitbulk)
- [Options](#options) · [Diagnostics](#diagnostics)
- [How it works](#how-it-works) · [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Toolkit.Audit.PostgreSQL   # or EF.Toolkit.Audit.SqlServer
```

The provider package brings this one with it. For the audit table's column types on each engine, and
how the payload is indexed, see the notes for
[PostgreSQL](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit-postgresql.md) and
[SQL Server](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit-sqlserver.md).

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseAuditing(a => a
        .Schema("audit")
        .ActorFrom<ICurrentUser>(u => new AuditActor(u.Id, u.Name))
        .MultiTenant(t => t.FromEntityProperty())));
```

That is the whole integration. The audit table is added to the model, so `dotnet ef migrations add`
picks it up, and the capture pipeline is registered.

If you reference both provider packages, use `UseNpgsqlAuditing()` or `UseSqlServerAuditing()`.

---

## Registering entity types

**Opt-in by default.** A type is audited when it says so:

```csharp
modelBuilder.Entity<Order>().IsAudited();     // fluent — preferred
```

```csharp
[Audited] public class Order { … }            // attribute — supported, discouraged
```

**One switch inverts the model-wide default:**

```csharp
.UseAuditing(a => a.AuditAllEntities())
```

after which every mapped type is audited and exclusion is what must be stated:

```csharp
modelBuilder.Entity<Session>().IsNotAudited();
[NotAudited] public class Session { … }
```

Both forms are meaningful under either default, so flipping the default later cannot silently start
or stop auditing a type somebody had already decided about. Keyless types are skipped; `IsAudited()`
on one is refused, because its entries would identify no row.

## Registering properties

**Every mapped property of an audited type is captured.** There is no property-level opt-in, because
the failure that allows — a column added later and quietly missing from the trail — is the one an
audit log exists to prevent.

Narrowing is per property, and available both ways:

```csharp
modelBuilder.Entity<Order>().IsAudited(a => a
    .Operations(AuditOperations.Insert | AuditOperations.Update)
    .Exclude(o => o.InternalNotes)
    .Exclude(o => new { o.DraftJson, o.ScratchPad })
    .Mask(o => o.CardNumber)
    .Mask(o => o.Iban, v => Last4(v))          // custom redactor
    .KeyFrom(o => o.PublicId));
```

```csharp
public class Order
{
    [AuditIgnore] public string InternalNotes { get; set; }
    [AuditMask]   public string CardNumber { get; set; }
}
```

Plus a model-wide rule, so a new secret-bearing property is masked from the moment it is named:

```csharp
.MaskProperties(p => p.Name.EndsWith("Token"))
.MaskWith("***")
```

**Excluding is not masking.** An excluded property leaves no trace that it changed; a masked one
records the change and not the value — which is usually what a secret wants.

**Fluent wins, and a contradiction fails.** A type that is `IsAudited()` and `[NotAudited]`, or a
property that is masked fluently and `[AuditIgnore]`d, does not build. Naming both sides at startup
is cheaper than a documentation page about precedence.

---

## The actor

```csharp
a.ActorFrom<ICurrentUser>(u => new AuditActor(u.Id, u.Name))   // application services
a.ActorFrom<IAuditActorProvider>()                             // your own provider
a.Actor(() => new AuditActor("system"))                        // constant or delegate
a.RequireActor()                                               // refuse an entry with no actor
```

Resolved from the **application's** service provider, so a provider is free to depend on
`IHttpContextAccessor` like any other service.

## Ambient scopes

An actor provider only works where there is a request to read one from. A background job, a
migration and the explicit bulk API all have real actors and no ambient request:

```csharp
using (AuditScope.Begin(actor: "reprice-job", reason: "nightly reprice")
                 .With("batch", batchNumber))
{
    await context.BulkUpdateAsync(prices);
}
```

A scope wins over every configured provider, so a request-scoped default can be overridden for one
operation. Scopes nest, inner values win, metadata merges, and every entry written inside one shares
a correlation id — generated automatically if you do not supply one.

## Multi-tenancy

```csharp
.MultiTenant(t => t
    .FromEntityProperty()                  // "TenantId" on the audited entity
    .FromProvider<IAuditTenantProvider>()  // fallback for types that carry none
    .Require())                            // refuse an entry with no tenant
```

`FromEntityProperty()` reads the property off the audited entity **including a shadow property**,
which is the whole Finbuckle.MultiTenant integration: its `IsMultiTenant()` adds exactly a
`TenantId` shadow property and keeps it filled, so every multi-tenant entity already carries what
belongs on its audit entry. No reference to Finbuckle, and nothing else to configure.

For entity types that are not themselves multi-tenant, add a provider:

```csharp
public sealed class FinbuckleAuditTenantProvider(IMultiTenantContextAccessor accessor)
    : IAuditTenantProvider
{
    public ValueTask<string?> GetTenantIdAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult(accessor.MultiTenantContext?.TenantInfo?.Id);
}
```

```csharp
services.AddScoped<IAuditTenantProvider, FinbuckleAuditTenantProvider>();
// …
.MultiTenant(t => t.FromEntityProperty().FromProvider<IAuditTenantProvider>())
```

---

## The audit table

One table for every audited type, in its own schema by default.

| Column | PostgreSQL | SQL Server |
| --- | --- | --- |
| `Id` | per key type | per key type |
| `EntityType` | `text` | `nvarchar(256)` |
| `EntityKey` | `text` | `nvarchar(256)` |
| `Operation` | `integer` | `int` |
| `ActorId`, `ActorName`, `ActorType` | `text` | `nvarchar(256)` |
| `TenantId` | `text` | `nvarchar(256)` |
| `OccurredAt` | `timestamptz` | `datetimeoffset(7)` |
| `CorrelationId` | `uuid` | `uniqueidentifier` |
| `Source` | `text` | `nvarchar(256)` |
| `Changes` | **`jsonb`** | `nvarchar(max)` + `CHECK (ISJSON(…) = 1)` |

Indexed by default on `(EntityType, EntityKey, OccurredAt DESC)` — the history of one row —
plus actor, correlation, tenant (when multi-tenancy is configured), and the payload.

`EntityKey` is the primary key rendered as one string, with components escaped so two different
composite keys can never collide. The typed key is also written into the payload, so nothing is lost.

## The payload

```json
{
  "op": "update",
  "key": { "Id": 42 },
  "changed": ["Status", "Total"],
  "old": { "Status": "Pending", "Total": 10.00 },
  "new": { "Status": "Shipped", "Total": 12.50 },
  "meta": { "reason": "nightly reprice", "batch": 4 }
}
```

Inserts carry `new` only; deletes carry `old` only. **Only properties that actually changed appear
in an update** — EF marks a property modified when it is assigned the value it already held, so
values are compared with the property's own `ValueComparer` rather than trusted. An update where
nothing moved produces no entry at all.

Values go through the property's own value converter before being recorded, so an enum mapped to
text appears as text and a strongly-typed id as its underlying value. Store-type precision is not
applied — a `decimal` written to a `numeric(18,2)` column is recorded as the application supplied
it, which is also the value EF's change tracker keeps. Owned value objects mapped into their owner's table are
folded into the owner's entry under their navigation path (`"Address.City"`), rather than becoming a
second entry for the same row. Keys are sorted, so two write paths describing the same change
produce byte-identical JSON.

## Querying it

`old` and `new` are sibling objects rather than a per-property pair of the two, because that is the
shape a containment index can answer:

```sql
-- uses the GIN index
SELECT * FROM audit."AuditEntries"
WHERE "Changes" @> '{"new":{"Status":"Shipped"}}';

-- also indexed
SELECT * FROM audit."AuditEntries"
WHERE "Changes" -> 'changed' ? 'Status';
```

SQL Server has no index over a JSON document. Make one path searchable with a persisted computed
column instead:

```csharp
.UseSqlServerAuditing(a => a.IndexJsonPath("$.new.Status"))
```

---

## The entry key

A client-generated UUIDv7 by default: its leading bytes are a millisecond timestamp, so inserts land
at the end of the index the way a sequence's do rather than scattering across it. It is unique
across the several contexts that may share one audit table, and — because nothing has to be read
back — it lets a large audit insert go down a bulk copy path with no staging table and no sequence
reservation.

Keys generated within the same millisecond have no order between them; `Guid.CreateVersion7()` fills
the remaining bytes at random. That is index locality, not a total ordering — sort a trail by
`OccurredAt`, not by `Id`.

Bring your own scheme:

```csharp
a.Ids<string>(sp => sp.GetRequiredService<IUserFriendlyIdGenerator>().Generate("aud"))
a.IdsFrom<MyIdProvider, string>()
a.BigIntKeys()          // database identity, at the cost of a read-back per insert
```

`IAuditEntryIdProvider<TKey>` has a `Generate(Span<TKey>)` overload, so a generator that can produce
a run cheaply gets the chance — an audited bulk operation asks for as many keys as it wrote rows.

## Where entries go

```csharp
a.WriteToSameContext()          // default — same connection, same transaction, atomic
a.WriteToContext<AuditDb>()     // a dedicated audit context — not atomic
a.WriteTo<MyOutboxSink>()       // anything else
```

A dedicated audit context has its own connection and so its own transaction, which it cannot be
atomic across. Saying so is required rather than assumed:

```csharp
a.WriteToContext<AuditDb>().Atomicity(AuditAtomicity.BestEffort)
```

Configuring it without that is refused at startup — the guarantee should be given up on purpose, not
by accident.

A custom `IAuditSink` is compatible with either atomicity, because only the sink knows where it
writes. Under `SameTransaction` it is handed the change's transaction on `AuditWriteContext` and is
expected to use it — a sink that writes to another table in the same database is then as atomic as
the built-in one, and throwing from it rolls the change back.

## Several DbContexts

A modular monolith with several contexts over one database can share one audit table. Exactly one
context owns its migrations:

```csharp
// Billing — owns the audit schema
.UseAuditing(a => a.Schema("audit"))

// Catalogue, Shipping — same table, no DDL
.UseAuditing(a => a.Schema("audit").SharedAuditTables())
```

`SharedAuditTables()` marks the entity type `ExcludeFromMigrations`, so `dotnet ef migrations add`
from a non-owner emits nothing for it. Without it, every context scaffolds a migration creating the
same table and only the first ever applies cleanly.

Entry keys are client-generated and time-ordered, so contexts writing concurrently cannot collide.

---

## With EF.Toolkit.Bulk

Transparent mode needs nothing: `UseBulkOperations()` replaces a service *below* `SaveChanges`, so
an accelerated save is audited by `UseAuditing()` alone.

The **explicit** API is different. `BulkInsertAsync` and its siblings bypass the change tracker
entirely — that is where their advantage comes from — so no `SaveChanges` interceptor sees them. One
line closes that gap:

```csharp
.UseNpgsql(cs)
.UseBulkOperations()
.UseAuditing(a => …)
.UseBulkAuditing()      // EF.Toolkit.Audit.Bulk
```

It throws at startup if either half is missing, so it cannot be forgotten quietly. Two things change:

- `BulkInsert` / `Update` / `Delete` / `Merge` / `Synchronize` produce audit entries, inside the
  operation's own transaction and after generated keys have landed.
- The entries are written through a bulk copy once there are enough of them, because auditing a
  hundred-thousand-row operation produces a hundred thousand entries.

**Before-images.** A detached object carries no earlier state, so an update or delete reads the rows
it is about to change first — one indexed read joined to the staging table the operation already
built, inside the same transaction, taking the row locks the write is about to take anyway. That is
what lets a bulk-updated row's entry carry the same old-to-new diff a `SaveChanges`-updated row's
does. It also settles a merge's insert-versus-update split per row, and captures the rows a
synchronise's delete arm is about to remove — which correspond to nothing the caller passed in and
would otherwise vanish without trace.

Opt out per call, knowing what it costs:

```csharp
await context.BulkUpdateAsync(orders, o => o.WithoutBeforeImages());   // new values only
await context.BulkDeleteAsync(orders, o => o.WithoutObservers());      // no entries at all
```

A merge or synchronise refuses `WithoutBeforeImages()`, because without them there is no way to say
which rows it inserted and which it updated.

---

## Options

```csharp
.UseAuditing(a => a
    .Schema("audit")
    .TableName("AuditEntries")
    .SharedAuditTables()                          // another context owns the migrations
    .AuditAllEntities()                           // invert the model-wide default
    .Operations(AuditOperations.All)
    .PayloadNames(AuditPayloadNames.Property)     // or .Column
    .StoreEntityTypeAs(AuditEntityTypeNames.Name) // or .FullName, .TableName
    .MaxValueLength(4096)
    .MaskWith("***")
    .MaskProperties(p => p.Name.EndsWith("Token"))
    .Atomicity(AuditAtomicity.SameTransaction)    // or .BestEffort
    .OnAuditFailure(AuditFailure.Throw)           // or .Ignore
    .BatchThreshold(100)
    .CaptureBeforeImages(true)
    .Indexes(AuditIndexes.All)
    .Json(jsonSerializerOptions)
    .UseTimeProvider(TimeProvider.System))
```

`OnAuditFailure(AuditFailure.Throw)` is the default, and under `SameTransaction` it rolls the change
back with the entry. An audit log that silently drops entries is worse than one that stops the
write, because the gap is invisible until somebody needs the entry that is not there.

## Diagnostics

Auditing is the kind of feature that is assumed to be working until somebody looks for an entry that
is not there. `EF.Toolkit.Audit` publishes to a `DiagnosticListener` named `EF.Toolkit.Audit`:

| Event | Payload | Answers |
| --- | --- | --- |
| `EntriesCaptured` | `EntriesCapturedEvent` | is it capturing? |
| `EntriesWritten` | `EntriesWrittenEvent` | is it writing, and did the batch writer engage? |
| `AuditSkipped` | `AuditSkippedEvent` | why did this change produce no entry? |
| `SinkFailed` | `SinkFailedEvent` | what went wrong, under either failure policy? |

---

## How it works

**`SaveChanges`** is captured by an `ISaveChangesInterceptor`, split across two points because
neither alone has everything an entry needs. Before the save, the values are all there but a
store-generated key is a placeholder. After it, the keys are real but original values have been
overwritten and deleted entries detached. So the change set is snapshotted in `SavingChanges` and
generated values re-read in `SavedChanges`.

Which leaves the transaction. Where none exists and the configured atomicity asks for one, the
interceptor opens it before the save — EF then uses it and does not commit a transaction it did not
create — and commits it after the entries are written.

**The explicit bulk API** is captured through `IBulkWriteObserver`, a public seam in
`EF.Toolkit.Bulk` that has nothing to do with auditing and is equally useful for an outbox or a
cache. Observers run after the write and after generated values land, inside the transaction.

**Both paths build entries through the same `IAuditEntryFactory`**, which is why an entry says
nothing about how the change was made beyond its `Source` column — and is what the equivalence suite
asserts by requiring the two to be byte-identical.

---

## Limitations

- `ExecuteUpdate` and `ExecuteDelete` are invisible to both the change tracker and the bulk
  pipeline, so they cannot be audited. This is true of every comparable library.
- Under `Atomicity(SameTransaction)`, a context configured with a **retrying execution strategy**
  must run its save inside `Database.CreateExecutionStrategy().ExecuteAsync(...)` with its own
  transaction — which auditing then joins. Such a strategy refuses a transaction opened from an
  interceptor, and EF's own is created and committed out of reach. Saying so beats opening a second
  transaction while the configuration claims otherwise. `BestEffort` has no such requirement.
- A bulk `Synchronize`'s delete arm is audited from a pre-read, so its entries describe the rows as
  they were at that moment rather than as returned by the delete itself.
- Owned **collections** have tables and keys of their own, so they are ordinary entity types as far
  as auditing is concerned and must be registered like any other. Only owned references that share
  their owner's table are folded.
- A custom redactor is a delegate held on a model annotation, so an entity type configured that way
  cannot be part of a compiled model. The mask-token form has no such restriction.
- SQL Server has no index over a JSON document. `IndexJsonPath(...)` makes one path searchable
  through a persisted computed column; there is no equivalent of PostgreSQL's GIN index over the
  whole payload.
- Native table partitioning for retention is not modelled — EF migrations do not describe it. Add it
  in a migration with raw SQL against the audit table.

## Sample

A runnable walkthrough lives in `samples/EF.Toolkit.Audit.Sample`. It starts its own PostgreSQL in
Docker, so it needs no setup:

```bash
dotnet run --project samples/EF.Toolkit.Audit.Sample
```

## License

MIT
