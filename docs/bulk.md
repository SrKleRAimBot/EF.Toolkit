# EF.Toolkit.Bulk

High-performance bulk operations for [EF Core](https://learn.microsoft.com/ef/core) — an
open-source alternative to the commercial [Entity Framework Extensions](https://entityframework-extensions.net/).

EF.Toolkit.Bulk **extends** EF Core from the outside. It is not a fork, and it does not bundle EF — you
install it alongside whatever EF Core version your app already uses.

- **Transparent.** `SaveChanges()` gets faster with no call-site changes.
- **Explicit when you want it.** `BulkInsert` / `BulkUpdate` / `BulkDelete` / `BulkMerge` /
  `BulkSynchronize` for large sets, up to **10x faster and 44x less memory**.
- **Change tracking preserved.** Store-generated keys are propagated and entities end in the state
  stock EF would have left them in.
- **Correct by construction.** Foreign-key ordering is inherited from EF's own dependency analysis;
  anything EF.Toolkit.Bulk cannot accelerate falls back to stock EF rather than changing results.

> **Status: pre-release.** Under active development toward `10.0.0`.

---

## Contents

- [Install](#install) · [Setup](#setup)
- [Which mode do I want?](#which-mode-do-i-want)
- [Transparent mode](#transparent-mode)
- [Explicit API](#explicit-api) — [insert](#insert) · [update](#update) · [delete](#delete) ·
  [merge](#merge-upsert) · [synchronize](#synchronize) · [graphs](#writing-a-whole-graph)
- [Change tracking](#change-tracking) · [Options](#options) · [Diagnostics](#diagnostics)
- [Performance](#performance)
- [How it works](#how-it-works) — [observing a write](#observing-a-write) · [correctness](#correctness)
- [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Toolkit.Bulk.PostgreSQL   # or EF.Toolkit.Bulk.SqlServer
```

The provider package brings this one with it. For how each engine executes a batch, and what differs
between them, see the notes for
[PostgreSQL](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk-postgresql.md) and
[SQL Server](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk-sqlserver.md).

| Package | EF Core | TFM |
| --- | --- | --- |
| `EF.Toolkit.Bulk 10.x` | 10.x | `net10.0` |
| `EF.Toolkit.Bulk 9.x` | 9.x | `net8.0` |

The version tracks EF Core's, because EF.Toolkit.Bulk hooks low-level update-pipeline services and must
never resolve across an EF major.

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseBulkOperations());
```

That is the whole integration. Everything below is available from that point.

If you reference *both* provider packages, `UseBulkOperations` is ambiguous — use the explicit
aliases `UseNpgsqlBulk()` or `UseSqlServerBulk()` instead.

---

## Which mode do I want?

| | Transparent `SaveChanges()` | Explicit `BulkInsertAsync` etc. |
| --- | --- | --- |
| Code changes | none | call the method |
| Ordering (foreign keys) | handled by EF | yours, or `IncludeGraph()` |
| Change tracking | full, as always | opt in with `Track()` |
| Interceptors | `ISaveChangesInterceptor` fires | not a `SaveChanges` |
| Typical speedup | 1.9–7.5x | 3.4–18x |
| Best for | existing code, mixed graphs | loading data |

Use transparent mode always — it is free and never changes results. Reach for the explicit API when
you are *loading data* rather than saving a graph you have been working with.

---

## Transparent mode

No call-site change. Existing code just gets faster:

```csharp
context.AddRange(orders);           // 50k orders, 200k lines
await context.SaveChangesAsync();   // COPY / SqlBulkCopy, FK-ordered, keys propagated
```

Inserts, updates and deletes are all accelerated, including entities with concurrency tokens.
Everything `SaveChanges()` guarantees still holds: dependency ordering, change tracking, and
`ISaveChangesInterceptor`.

`BulkSaveChangesAsync()` exists as a synonym, because it is the name people look for — but once
`UseBulkOperations()` is applied every save already goes through EF.Toolkit.Bulk.

---

## Explicit API

These skip EF's change tracking and command pipeline entirely, reading values straight off your
objects. Every method has a synchronous overload, and `BulkInsertAsync` also accepts an
`IAsyncEnumerable<T>`.

### Insert

```csharp
BulkResult result = await context.BulkInsertAsync(orders);

orders[0].Id;                    // populated — keys are always written back
context.Entry(orders[0]).State;  // Detached — tracking is separate, and opt-in
result.Inserted;                 // 50000
```

### Update

```csharp
await context.BulkUpdateAsync(orders);
```

Matches on the primary key. Note that it writes **every** non-key column, not just the ones you
changed — there is no change tracking involved to tell it otherwise. A row that has gone missing
raises `DbUpdateConcurrencyException` naming the affected entities, as it would under stock EF.

`Include` and `Exclude` narrow that back down when you do know which columns you touched:

```csharp
await context.BulkUpdateAsync(orders, o => o.Include(x => new { x.Status, x.ShippedAt }));
await context.BulkUpdateAsync(orders, o => o.Exclude(x => x.CreatedAt));
```

`MatchOn` locates rows by something other than the primary key — useful when the input never had
one, as with rows read from a file:

```csharp
await context.BulkUpdateAsync(prices, o => o.MatchOn(p => new { p.Sku, p.Region }));
```

Only the **primary** key locates a row by default. An alternate key — `HasAlternateKey(...)` — is an
ordinary writable column here, so an update can change one; name it in `MatchOn` if you want to
locate rows by it instead.

Unlike a merge this needs **no unique index**: a set-based `UPDATE` is content for one source row to
reach several target rows, and will. `BulkResult` then reports how many of the entities you passed
found a row, not how many rows changed — and a source row that matched nothing is still a
`DbUpdateConcurrencyException`.

### Delete

```csharp
await context.BulkDeleteAsync(orders);   // only the keys are read
```

The entities end up `Detached` whatever the tracking setting says: their rows no longer exist, so
leaving them `Unchanged` would assert otherwise. `MatchOn` works here too, with the same
one-to-many caveat — deleting every row of an import batch takes one entity carrying the batch id.

### Merge (upsert)

```csharp
var result = await context.BulkMergeAsync(customers, o => o.MatchOn(c => c.Email));
Console.WriteLine($"{result.Inserted} new, {result.Updated} existing");
```

The database decides insert-versus-update per row — `INSERT … ON CONFLICT` on PostgreSQL, `MERGE`
on SQL Server — so unlike a read-then-write loop there is no window for another writer to slip in.

`MatchOn` defaults to the primary key and accepts a composite:

```csharp
o.MatchOn(c => new { c.TenantId, c.Email })
```

**The match columns must have a unique index.** `ON CONFLICT` needs one to define what a conflict
*is*, and without one a `MERGE` would happily match several rows at once. Store-generated keys are
populated on the entities that turned out to be inserts.

`InsertOnly` names the columns that record how a row came to exist, so a merge writes them when it
inserts and leaves them alone when it updates:

```csharp
o.MatchOn(c => c.Email).InsertOnly(c => c.CreatedAt)
```

### Synchronize

```csharp
await context.BulkSynchronizeAsync(
    customers, o => o.MatchOn(c => c.Email).AllowFullTableDelete());
// → 25 inserted, 60 updated, 40 deleted
```

Makes the table match your list exactly. **The delete covers the whole table** — that is what makes
it a synchronise rather than a merge, and it is easy to trigger with a partial list by accident, so
it has to be confirmed. It refuses an empty source rather than emptying the table, and runs in one
transaction so the table is never seen half-synchronised.

Where your list is partial *by design* — one tenant, one partition, one import — scope the delete
instead of confirming it:

```csharp
await context.BulkSynchronizeAsync(rows, o => o
    .MatchOn(r => r.ExternalId)
    .WithinScope(r => r.TenantId == tenantId));
```

Now only rows inside the scope can be deleted, and `AllowFullTableDelete()` is not needed — the
delete is no longer full-table.

On an entity type with a [global query filter](https://learn.microsoft.com/ef/core/querying/filters)
a scope is not optional, and `AllowFullTableDelete()` will not substitute for one. The filter hides
rows from every read this context performs, so the source list could not have named them — but the
delete arm still reaches them. Give the scope the same predicate the filter applies, and the two
sides agree again.

The translator is deliberately narrow: `&&`-ed comparisons between a
mapped property and a value, with anything else refused by name rather than silently dropped, since
a dropped condition widens a delete. Values are bound as parameters. For a predicate it does not
cover there is a SQL overload, where the target is aliased `t` and every interpolation hole is bound
rather than pasted:

```csharp
o.WithinScope($"t.tenant_id = {tenantId} AND t.archived_at IS NULL")
```

### Writing a whole graph

By default you order writes yourself. `IncludeGraph()` does it for you: it follows navigations from
the entities you pass in, orders the entity types so principals come first, and fills in each
foreign key from its navigation once the principal has a key — the job change tracking would
normally do.

```csharp
foreach (var customer in customers)
{
    var order = new Order { Customer = customer, ... };    // no CustomerId
    order.Lines.Add(new OrderLine { Order = order, ... }); // no OrderId
    customer.Orders.Add(order);
}

// Only the customers are passed; orders and lines are reached through navigations.
await context.BulkInsertAsync(customers, o => o.IncludeGraph());
```

A table that references itself is layered by depth, because there the ordering depends on the data
rather than the schema. Two entity types that reference *each other* are rejected with a clear
error: breaking such a cycle needs a second pass that fills the foreign keys after both rows exist,
which `SaveChanges()` does and this does not.

The whole graph goes in one transaction — a half-written graph would leave dependents pointing at
principals that were never inserted.

---

## Change tracking

Store-generated keys are **always** written back onto your objects. Tracking is a separate concern,
and for the explicit API it is opt-in:

```csharp
await context.BulkInsertAsync(orders);   // detached input
orders[0].Id;                            // populated
context.Entry(orders[0]).State;          // Detached

await context.BulkInsertAsync(orders, o => o.Track());
context.Entry(orders[0]).State;          // Unchanged
```

The reason for the split is cost: writing a key onto an object takes microseconds, while a tracker
entry plus an original-values snapshot costs hundreds of bytes per row — on exactly the large loads
this API exists to serve.

Entities the context is **already** tracking are always reconciled to `Unchanged`, whatever the
setting. Left as `Added`, the next `SaveChanges()` would insert every row a second time.

---

## Options

Per call:

```csharp
await context.BulkInsertAsync(orders, o => o
    .BatchSize(20_000)
    .Track()
    .Timeout(TimeSpan.FromMinutes(5))
    .IncludeGraph()
    .OnProgress(p => log.LogInformation("{Done}/{Total}", p.Completed, p.Total)));
```

| Option | Applies to | Effect |
| --- | --- | --- |
| `MatchOn` | merge, synchronise, update, delete | locate rows by something other than the primary key |
| `Include` / `Exclude` | insert, update, merge | narrow the set of columns written |
| `InsertOnly` | merge, synchronise | write these when inserting, leave them alone when updating |
| `WithinScope` | synchronise | confine the delete arm to the rows it selects |
| `AllowFullTableDelete` | synchronise | confirm an unscoped, whole-table delete |
| `WithoutObservers` | all | hide this write from registered [observers](#observing-a-write) |
| `WithoutBeforeImages` | update, delete, merge, synchronise | skip the read that gives observers the rows as they were |

An option used outside the operations it applies to is refused, not ignored — including on
`IncludeGraph()`, which refuses `Include`/`Exclude` outright because a projection over the root says
nothing about the other entity types a graph insert writes.

Context-wide:

```csharp
.UseBulkOperations(b => b
    .Threshold(100)                                // rows before bulk engages in transparent mode
    .MaxBatchSize(50_000)
    .MergeCounts(MergeCounts.Exact)                // or .Approximate
    .StagingIndexThreshold(5_000)                  // rows before a staging table is indexed
    .ValidateConstraints(true)                     // CHECK/FK enforcement on bulk-copied rows
    .FireTriggers(true)                            // triggers fire for bulk-copied rows
    .OnUnsupported(Unsupported.FallBack))          // or .Throw, to assert the fast path in CI
```

`OnUnsupported(Unsupported.Throw)` is worth knowing about: it turns "this quietly ran at stock EF
speed" into a visible failure, so a regression in the fast path cannot hide as a performance problem
nobody notices.

<details>
<summary><strong>How the merge insert/update split is counted</strong></summary>

`BulkResult.Inserted` and `.Updated` are exact by default. SQL Server reports this for free through
`MERGE`'s `$action`; PostgreSQL has no equivalent, so EF.Toolkit.Bulk counts the rows that already exist
immediately before the merge, inside the same transaction.

That count is an indexed existence check and costs almost nothing — 116 ms versus 119 ms on the
10,000-row merge benchmark, inside the noise — so there is rarely a reason to change it. If you are
merging at a scale where it does show up:

```csharp
o.MatchOn(c => c.Email).MergeCounts(MergeCounts.Approximate)
```

`Approximate` reads each returned row's `xmax`, which PostgreSQL leaves at zero on a freshly
inserted tuple. That is a widely-used convention rather than a documented guarantee and can
misreport under concurrent access — but it is free. **Both settings write identical data**, and the
setting is ignored on SQL Server, which is exact either way.

On **PostgreSQL 17 and later the setting does nothing either**: `MERGE … RETURNING merge_action()`
reports the split exactly, per row, at no cost, so neither the pre-merge count nor the `xmax`
convention is needed. Detection is automatic; `UseMerge(false)` forces the older path if a pooler or
a PostgreSQL-compatible engine reports a version whose capabilities it does not have.
</details>

---

## Diagnostics

`IDbCommandInterceptor` cannot see bulk writes: `COPY` and `SqlBulkCopy` are not `DbCommand`s. So
EF.Toolkit.Bulk publishes its own events, which is also how you confirm the fast path is engaging:

```csharp
DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
// listener name: "EF.Toolkit.Bulk"
```

| Event | Payload | Raised |
| --- | --- | --- |
| `EF.Toolkit.Bulk.PartitionsPlanned` | `PartitionsPlannedEvent` | once per batch, after grouping |
| `EF.Toolkit.Bulk.PartitionExecuted` | `PartitionExecutedEvent` | per partition, with `Accelerated` and `Duration` |
| `EF.Toolkit.Bulk.ExplicitFallback` | `ExplicitFallbackEvent` | an explicit call ran through EF Core instead |
| `EF.Toolkit.Bulk.StagingCleanupFailed` | `StagingCleanupFailedEvent` | a staging table could not be dropped |

`PartitionExecutedEvent.Accelerated` is the one to watch. A silent fallback is correct but slow, and
without this it looks identical to success.

---

## Performance

BenchmarkDotNet, three warmup iterations and fifteen measured, PostgreSQL 16 in Docker on an
M-series Mac. Read the ratios rather than the absolute times — a laptop running database containers
is not a quiet machine, and stock EF itself measured 25% slower between two runs of the same
baseline. Reproduce with:

```bash
dotnet run --project tests/EF.Toolkit.Bulk.Benchmarks -c Release -- --filter "*"
EFBULK_BENCH_ENGINE=sqlserver dotnet run --project tests/EF.Toolkit.Bulk.Benchmarks -c Release -- --filter "*"
```

**Insert**, five columns, server-generated keys:

| Rows | | Time | vs stock | Rows/sec | Allocated |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1,000 | `SaveChanges()`, stock EF | 62 ms | — | 16,145 | 8.3 MB |
| | `SaveChanges()`, EF.Toolkit.Bulk | 33 ms | **1.9x** | 30,569 | 4.3 MB |
| | `BulkInsertAsync` | 18 ms | **3.4x** | 54,980 | 0.21 MB |
| 10,000 | `SaveChanges()`, stock EF | 369 ms | — | 27,115 | 75.8 MB |
| | `SaveChanges()`, EF.Toolkit.Bulk | 132 ms | **2.8x** | 75,593 | 41.9 MB |
| | `BulkInsertAsync` | 51 ms | **7.3x** | 196,906 | 1.2 MB |
| 100,000 | `SaveChanges()`, stock EF | 3,735 ms | — | 26,770 | 748 MB |
| | `SaveChanges()`, EF.Toolkit.Bulk | 651 ms | **5.7x** | 153,648 | 412 MB |
| | `BulkInsertAsync` | 310 ms | **12.0x** | 322,360 | 11.6 MB |

**Insert**, thirty columns — decimals with scale, GUIDs, dates and times, long nullable text, a byte
array, and an enum through a value converter. This is the shape where per-column costs show:

| Rows | | Time | vs stock | Rows/sec | Allocated |
| ---: | --- | ---: | ---: | ---: | ---: |
| 10,000 | `SaveChanges()`, stock EF | 838 ms | — | 11,927 | 296 MB |
| | `SaveChanges()`, EF.Toolkit.Bulk | 122 ms | **6.9x** | 81,782 | 98.9 MB |
| | `BulkInsertAsync` | 56 ms | **14.9x** | 178,079 | 3.0 MB |
| 100,000 | `SaveChanges()`, stock EF | 9,019 ms | — | 11,087 | 2,943 MB |
| | `SaveChanges()`, EF.Toolkit.Bulk | 1,207 ms | **7.5x** | 82,882 | 982 MB |
| | `BulkInsertAsync` | 507 ms | **17.8x** | 197,138 | 28.1 MB |

**Update, delete and upsert** at 10,000 rows. Seeding and loading happen outside the measurement, so
only the write is timed:

| | Time | vs baseline | Rows/sec | Allocated |
| --- | ---: | ---: | ---: | ---: |
| Update — `SaveChanges()`, stock EF | 350 ms | — | 28,578 | 46.2 MB |
| Update — `SaveChanges()`, EF.Toolkit.Bulk | 104 ms | **3.3x** | 95,701 | 24.4 MB |
| Update — `BulkUpdateAsync` | 86 ms | **4.1x** | 116,510 | 1.4 MB |
| Delete — `SaveChanges()`, stock EF | 255 ms | — | 39,258 | 36.4 MB |
| Delete — `SaveChanges()`, EF.Toolkit.Bulk | 40 ms | **6.4x** | 251,665 | 24.2 MB |
| Delete — `BulkDeleteAsync` | 26 ms | **9.9x** | 388,112 | 0.39 MB |
| Upsert — read-then-decide, then `SaveChanges()` | 397 ms | — | 25,200 | 65.7 MB |
| Upsert — `BulkMergeAsync` | 139 ms | **2.8x** | 71,779 | 8.2 MB |

EF Core has no upsert, so the merge baseline is what an application actually writes by hand: load
the existing rows, decide per item whether to add or update, save.

### Reading these numbers

**Memory is the bigger story, and it grows with the row.** At 100,000 five-column rows stock EF
allocates 748 MB against `BulkInsertAsync`'s 11.6 MB. Widen the row to thirty columns and stock EF
allocates **2.9 GB** — triggering eleven gen-2 collections — where `BulkInsertAsync` allocates
**28 MB** and triggers none. On a memory-constrained host that is the difference between working and
not.

**The wide table is where per-cell work shows.** The explicit API compiles a delegate per column
that carries a value from the property to the wire in its own type, so nothing is boxed on the way;
that is worth 3x the allocations at thirty columns and almost nothing at five, which is exactly why
the narrow table alone was a misleading benchmark.

**Transparent mode improves with scale** — it replaces how a batch is executed, but everything
before that still happens: change detection, a modification command per row, and the dependency
ordering that keeps foreign keys safe. That cost is largely fixed, so its share shrinks as the row
count grows. It cannot reach the explicit API's allocation figures at any size, because EF has
already boxed every value into a modification command before this library sees the row.

**The explicit API is bounded by your database, not by EF.Toolkit.Bulk.** At 100,000 rows a third of
the insert time is index maintenance no client-side change can touch. That is the healthy outcome:
at scale the remaining cost is work the database genuinely has to do.

## How it works

**Transparent mode** replaces `IModificationCommandBatchFactory` through EF's public
`ReplaceService`. That places EF.Toolkit.Bulk *downstream* of `ICommandBatchPreparer`, which has already
built a dependency multigraph over the modification commands, topologically sorted it, and filled
each batch from a single dependency-independent set. Ordering correctness is therefore inherited
rather than re-derived — regrouping commands within a batch cannot violate a foreign key.

Each batch is grouped by `(table, state, column shape)` and dispatched to a provider executor:

| | PostgreSQL | SQL Server |
| --- | --- | --- |
| Insert | binary `COPY` | `SqlBulkCopy` |
| Generated keys | sequence values reserved up front | staging table + `MERGE … OUTPUT` |
| Update / delete | staging + `UPDATE … FROM` / `DELETE … USING`, `RETURNING` | staging + `UPDATE/DELETE … FROM`, `OUTPUT` |
| Upsert | `MERGE … RETURNING merge_action()` on 17+, else `INSERT … ON CONFLICT … DO UPDATE` | `MERGE` with `$action` |
| Synchronize | `WHEN NOT MATCHED BY SOURCE` on 17+, else a second `DELETE … WHERE NOT EXISTS` | `WHEN NOT MATCHED BY SOURCE THEN DELETE` |

Update, delete and merge all carry a **source ordinal** through the staging table, so a returned row
maps back to the row that produced it by position rather than by matching key values. SQL Server's
`OUTPUT` may name a table from the statement's `FROM` clause, and PostgreSQL's `RETURNING` may name
one from `FROM` or `USING`, so both engines can hand it back directly.

`INSERT … ON CONFLICT … RETURNING` is the exception: it can only name columns of the target row,
never the source. That is also why PostgreSQL reserves sequence values for generated keys before
version 17 — a staged insert has no documented way to map them back otherwise. PostgreSQL 17's
`MERGE … RETURNING` *can* see the source, so on 17 and later neither the reservation nor the
insert-versus-update counting workarounds are needed. SQL Server has no sequence behind an
`IDENTITY` column, so it stages with an ordinal on every version.

A staging table is analysed before it is joined, and indexed on its join columns above
`StagingIndexThreshold` rows. A freshly loaded temporary table has no statistics — autovacuum never
touches one on PostgreSQL — so without that the planner joins it to the target on a guess.

**The explicit API** skips that pipeline entirely, reading values through accessors compiled once
per entity type and cached against the model. It takes on ordering itself: a topological sort of
entity types over the model's foreign keys, cached per model, with row-level layering only where a
table references itself.

**Anything unsupported falls back.** Stored-procedure mappings, JSON column updates, and partitions
below the threshold are replayed through a genuine provider batch. Bulk is an optimisation and must
never change results.

### Observing a write

The explicit API bypasses the change tracker, which is where its advantage comes from and also why
`ISaveChangesInterceptor` never fires for it. Anything that needs to react to a write — an outbox, a
cache invalidation, an audit trail — therefore has nowhere to stand. `IBulkWriteObserver` is that
place:

```csharp
public interface IBulkWriteObserver
{
    BulkObservationNeeds Observes(IEntityType entityType, BulkOperationKind operation);
    ValueTask ObservedAsync(BulkWriteObservation observation, CancellationToken cancellationToken);
}
```

Register one in EF Core's internal service provider from an `IDbContextOptionsExtension`. Observers
run after the write has succeeded and after store-generated values have been written back, but
before the transaction commits — so their work is atomic with the write, and throwing rolls it back.

An observer that asks for `BulkObservationNeeds.BeforeImages` gets the rows as they stood: one
indexed read joined to the staging table the operation already built, issued inside the same
transaction and taking the row locks the write is about to take anyway. It also settles two things
the statement itself only reports in total — which rows a merge inserted rather than updated, and
which rows a synchronise's delete arm removed that no source row named.

When nothing is registered the cost is a single null check, and the extra read is only ever issued
when something is going to use it. `EF.Toolkit.Audit.Bulk` is one implementation; see the
[audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md).

### Correctness

The primary gate is a differential harness: every scenario runs against two structurally identical
databases — once through stock EF, once through EF.Toolkit.Bulk — comparing raw table contents (read over
ADO, not through EF, so a self-consistently wrong conversion cannot hide), full change-tracker state
including original values, and failure behaviour. Four negative controls prove the harness detects
divergence rather than silently comparing nothing.

289 tests run against PostgreSQL 16, PostgreSQL 17 and SQL Server 2022.

---

## Limitations

- `IDbCommandInterceptor` does not fire for `COPY` / `SqlBulkCopy` paths — these are not
  `DbCommand`s. EF.Toolkit.Bulk emits [its own events](#diagnostics) instead. `ISaveChangesInterceptor` is
  unaffected.
- `ISaveChangesInterceptor` does not fire for the explicit API either, which is not a save.
  [`IBulkWriteObserver`](#observing-a-write) is the seam for anything that needs to react to those
  writes.
- JSON-column and stored-procedure-mapped writes always take the stock EF path.
- The explicit API cannot read shadow properties — it works from your objects, and there is no entry
  to read them from. Use `SaveChanges()` for those entity types.
- `IncludeGraph()` rejects two entity types that reference each other; breaking such a cycle needs a
  second pass that `SaveChanges()` performs and this does not.
- Sequence block reservation consumes values on rollback — the same behaviour as stock EF, since
  sequence allocation is non-transactional. PostgreSQL 17 and later do not reserve at all.
- The explicit API refuses an entity type with a concurrency token on every operation that has to
  locate an existing row — `BulkUpdateAsync`, `BulkDeleteAsync`, `BulkMergeAsync` and
  `BulkSynchronizeAsync`. Checking a token needs the value as it was loaded, and the explicit API
  works from detached objects that do not carry it — for the usual load-increment-save pattern the
  token already holds the *new* value. `BulkInsertAsync` is unaffected: an insert locates no row, so
  it writes the token like any other column. `SaveChanges()` tracks the before-image and handles all
  of these correctly.
- The explicit API refuses an entity type mapped to more than one table — table-per-type inheritance
  and entity splitting — because one bulk statement writes one table, and the rest of the row would
  be left behind. Table-per-hierarchy is fine, as long as the discriminator is mapped to a property
  rather than left as a shadow one.
- The explicit API refuses an entity type that shares its table with another entity type outside its
  hierarchy: an owned reference, an owned type mapped to JSON, or table splitting. Those columns are
  absent from the owner's own mappings, so the row would come out half-populated. An owned collection
  in a table of its own is not affected — the owner's row is complete, and the owned rows are written
  the way any dependent is. [Complex types](https://learn.microsoft.com/ef/core/modeling/complex-types)
  are *not* affected either: they are not separate entity types, and their columns are written.
- The explicit API refuses an update or a delete on a keyless entity type. With no key there is
  nothing to put in the `WHERE` clause, so the statement would apply to every row in the table. Name
  the columns that identify a row with `MatchOn` if there are any; an insert needs no key and is
  allowed as it stands.
- The explicit API refuses a property-bag entity type — in practice the implicit join entity behind a
  many-to-many skip navigation, which EF models as a `Dictionary<string, object>` with no members to
  read. Declare the join entity explicitly with `UsingEntity<T>()` to bulk-write it.
- The explicit API refuses an entity type mapped to a SQL Server temporal table. Its period columns
  are shadow properties the provider maintains, and there is nothing on your objects to read them
  from. `SaveChanges()` handles these, still accelerated, and history is recorded as usual.
- `BulkSynchronizeAsync` requires either `WithinScope(...)` or `AllowFullTableDelete()`. Unscoped,
  its delete arm covers the whole table, so a partial list removes everything the list omitted.
- `BulkSynchronizeAsync` requires `WithinScope(...)` specifically — `AllowFullTableDelete()` is not
  enough — on an entity type with a [global query filter](https://learn.microsoft.com/ef/core/querying/filters).
  Its delete arm reaches the rows the filter hides, which the context cannot read and the source
  therefore could never have named. Scope it with the same predicate the filter applies:
  `.WithinScope(e => e.TenantId == tenantId)`. The other verbs are unaffected — they touch only the
  rows you hand over, located by key, exactly as `SaveChanges()` does, and stock EF applies no query
  filter there either.
- `WithinScope`'s expression translator takes `&&`-ed comparisons between a mapped property and a
  value, and nothing else. Widening it is not a priority: the SQL overload covers the rest, and a
  scope that quietly fails to translate part of a predicate deletes more than it was asked to.
- Column projection applies to the explicit API only, and not to `IncludeGraph()`. On the
  transparent path EF decides the write set from change tracking, which is already narrower; a graph
  insert writes several entity types, and a projection over one says nothing about the rest.
- Rows of one table but differing column shape — a column with a database default, set on some rows
  and left null on others — are written shape by shape, so store-generated keys are handed out in a
  different order than `SaveChanges()` assigns them. Every row keeps its own key and its own values;
  only the numbering differs.
- A bulk copy cannot enlist in a `DbTransaction` wrapped by a profiler or tracing decorator. Those
  writes fall back to stock EF rather than escaping the transaction.

## Sample

A runnable walkthrough of every operation lives in `samples/EF.Toolkit.Bulk.Sample`. It starts its own
PostgreSQL in Docker, so it needs no setup:

```bash
dotnet run --project samples/EF.Toolkit.Bulk.Sample
dotnet run --project samples/EF.Toolkit.Bulk.Sample -- "Host=localhost;Database=shop;Username=postgres;Password=postgres"
```

## License

MIT
