# EF.Bulk

High-performance bulk operations for [EF Core](https://learn.microsoft.com/ef/core) — an
open-source alternative to the commercial [Entity Framework Extensions](https://entityframework-extensions.net/).

EF.Bulk **extends** EF Core from the outside. It is not a fork, and it does not bundle EF — you
install it alongside whatever EF Core version your app already uses.

- **Transparent.** `SaveChanges()` gets faster with no call-site changes.
- **Explicit when you want it.** `BulkInsert` / `BulkUpdate` / `BulkDelete` / `BulkMerge` /
  `BulkSynchronize` for large sets, up to **10x faster and 44x less memory**.
- **Change tracking preserved.** Store-generated keys are propagated and entities end in the state
  stock EF would have left them in.
- **Correct by construction.** Foreign-key ordering is inherited from EF's own dependency analysis;
  anything EF.Bulk cannot accelerate falls back to stock EF rather than changing results.

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
- [How it works](#how-it-works) · [Limitations](#limitations)

---

## Install

```bash
dotnet add package EF.Bulk.PostgreSQL   # or EF.Bulk.SqlServer
```

| Package | EF Core | TFM |
| --- | --- | --- |
| `EF.Bulk 10.x` | 10.x | `net10.0` |
| `EF.Bulk 9.x` | 9.x | `net8.0` |

The version tracks EF Core's, because EF.Bulk hooks low-level update-pipeline services and must
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
| Typical speedup | 1.5–4.7x | 4–13x |
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
`UseBulkOperations()` is applied every save already goes through EF.Bulk.

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

### Delete

```csharp
await context.BulkDeleteAsync(orders);   // only the keys are read
```

The entities end up `Detached` whatever the tracking setting says: their rows no longer exist, so
leaving them `Unchanged` would assert otherwise.

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

### Synchronize

```csharp
await context.BulkSynchronizeAsync(customers, o => o.MatchOn(c => c.Email));
// → 25 inserted, 60 updated, 40 deleted
```

Makes the table match your list exactly. **The delete covers the whole table** — that is what makes
it a synchronise rather than a merge, and it is easy to trigger with a partial list by accident. It
refuses an empty source rather than emptying the table, and runs in one transaction so the table is
never seen half-synchronised.

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

Context-wide:

```csharp
.UseBulkOperations(b => b
    .Threshold(100)                                // rows before bulk engages in transparent mode
    .MaxBatchSize(50_000)
    .KeyAllocation(KeyAllocation.ReserveBlocks)    // or .Staging
    .MergeCounts(MergeCounts.Exact)                // or .Approximate
    .OnUnsupported(Unsupported.FallBack))          // or .Throw, to assert the fast path in CI
```

`OnUnsupported(Unsupported.Throw)` is worth knowing about: it turns "this quietly ran at stock EF
speed" into a visible failure, so a regression in the fast path cannot hide as a performance problem
nobody notices.

<details>
<summary><strong>How the merge insert/update split is counted</strong></summary>

`BulkResult.Inserted` and `.Updated` are exact by default. SQL Server reports this for free through
`MERGE`'s `$action`; PostgreSQL has no equivalent, so EF.Bulk counts the rows that already exist
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
</details>

---

## Diagnostics

`IDbCommandInterceptor` cannot see bulk writes: `COPY` and `SqlBulkCopy` are not `DbCommand`s. So
EF.Bulk publishes its own events, which is also how you confirm the fast path is engaging:

```csharp
DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
// listener name: "EFBulk"
```

| Event | Payload | Raised |
| --- | --- | --- |
| `EFBulk.PartitionsPlanned` | `PartitionsPlannedEvent` | once per batch, after grouping |
| `EFBulk.PartitionExecuted` | `PartitionExecutedEvent` | per partition, with `Accelerated` and `Duration` |
| `EFBulk.ExplicitFallback` | `ExplicitFallbackEvent` | an explicit call ran through EF Core instead |
| `EFBulk.StagingCleanupFailed` | `StagingCleanupFailedEvent` | a staging table could not be dropped |

`PartitionExecutedEvent.Accelerated` is the one to watch. A silent fallback is correct but slow, and
without this it looks identical to success.

---

## Performance

BenchmarkDotNet, five iterations after warmup, PostgreSQL 16 in Docker on an M-series Mac. Read the
ratios rather than the absolute times, and reproduce with:

```bash
dotnet run --project tests/EF.Bulk.Benchmarks -c Release -- --filter "*"
```

**Insert**, one table, server-generated keys:

| Rows | | Time | vs stock | Allocated |
| ---: | --- | ---: | ---: | ---: |
| 1,000 | `SaveChanges()`, stock EF | 53 ms | — | 8.3 MB |
| | `SaveChanges()`, EF.Bulk | 32 ms | **1.7x** | 4.9 MB |
| | `BulkInsertAsync` | 7.9 ms | **6.8x** | 0.24 MB |
| 10,000 | `SaveChanges()`, stock EF | 385 ms | — | 75.8 MB |
| | `SaveChanges()`, EF.Bulk | 262 ms | **1.5x** | 46.3 MB |
| | `BulkInsertAsync` | 41 ms | **9.3x** | 1.8 MB |
| 100,000 | `SaveChanges()`, stock EF | 2,620 ms | — | 748 MB |
| | `SaveChanges()`, EF.Bulk | 627 ms | **4.2x** | 456 MB |
| | `BulkInsertAsync` | 266 ms | **9.9x** | 16.9 MB |

**Update, delete and upsert** at 10,000 rows. Seeding and loading happen outside the measurement, so
only the write is timed:

| | Time | vs baseline | Allocated |
| --- | ---: | ---: | ---: |
| Update — `SaveChanges()`, stock EF | 319 ms | — | 45.0 MB |
| Update — `SaveChanges()`, EF.Bulk | 143 ms | **2.2x** | 33.0 MB |
| Update — `BulkUpdateAsync` | 73 ms | **4.4x** | 7.5 MB |
| Delete — `SaveChanges()`, stock EF | 277 ms | — | 33.2 MB |
| Delete — `SaveChanges()`, EF.Bulk | 135 ms | **2.1x** | 31.2 MB |
| Delete — `BulkDeleteAsync` | 22 ms | **12.6x** | 6.5 MB |
| Upsert — read-then-decide, then `SaveChanges()` | 435 ms | — | 65.9 MB |
| Upsert — `BulkMergeAsync` | 120 ms | **3.6x** | 9.6 MB |

EF Core has no upsert, so the merge baseline is what an application actually writes by hand: load
the existing rows, decide per item whether to add or update, save.

### Reading these numbers

**Memory is the bigger story.** At 100,000 rows stock EF allocates **748 MB** and triggers gen-2
collections; `BulkInsertAsync` allocates **17 MB** — 44x less — and triggers none. On a memory-constrained host
that is the difference between working and not.

**Transparent mode improves with scale** — 1.5x at small sizes, 4.2x at 100,000 rows. It replaces
how a batch is executed, but everything before that still happens: change detection, a modification
command per row, and the dependency ordering that keeps foreign keys safe. That cost is largely
fixed, so its share shrinks as the row count grows.

**The explicit API is bounded by your database, not by EF.Bulk.** At 100,000 rows, dropping the
unique index from the benchmark table takes the same insert from ~253 ms to a 160 ms median — about
a third of the time is index maintenance no client-side change can touch. That is the healthy
outcome: at scale the remaining cost is the work the database genuinely has to do.

---

## How it works

**Transparent mode** replaces `IModificationCommandBatchFactory` through EF's public
`ReplaceService`. That places EF.Bulk *downstream* of `ICommandBatchPreparer`, which has already
built a dependency multigraph over the modification commands, topologically sorted it, and filled
each batch from a single dependency-independent set. Ordering correctness is therefore inherited
rather than re-derived — regrouping commands within a batch cannot violate a foreign key.

Each batch is grouped by `(table, state, column shape)` and dispatched to a provider executor:

| | PostgreSQL | SQL Server |
| --- | --- | --- |
| Insert | binary `COPY` | `SqlBulkCopy` |
| Generated keys | sequence values reserved up front | staging table + `MERGE … OUTPUT` |
| Update / delete | staging + `UPDATE … FROM` / `DELETE … USING`, `RETURNING` | staging + `UPDATE/DELETE … FROM`, `OUTPUT` |
| Upsert | `INSERT … ON CONFLICT … DO UPDATE` | `MERGE` with `$action` |
| Synchronize | plus `DELETE … WHERE NOT EXISTS` | `WHEN NOT MATCHED BY SOURCE THEN DELETE` |

The key-generation split is not arbitrary. PostgreSQL's `RETURNING` cannot reference the staging
table, so before PostgreSQL 17 a staged insert has no documented way to map generated keys back to
the rows that produced them — reserving sequence values up front makes correlation exact on every
version. SQL Server has no sequence behind an `IDENTITY` column, but `MERGE`'s `OUTPUT` *can*
reference the source, so staging with an ordinal is exact there.

**The explicit API** skips that pipeline entirely, reading values through accessors compiled once
per entity type and cached against the model. It takes on ordering itself: a topological sort of
entity types over the model's foreign keys, cached per model, with row-level layering only where a
table references itself.

**Anything unsupported falls back.** Stored-procedure mappings, JSON column updates, and partitions
below the threshold are replayed through a genuine provider batch. Bulk is an optimisation and must
never change results.

### Correctness

The primary gate is a differential harness: every scenario runs against two structurally identical
databases — once through stock EF, once through EF.Bulk — comparing raw table contents (read over
ADO, not through EF, so a self-consistently wrong conversion cannot hide), full change-tracker state
including original values, and failure behaviour. Four negative controls prove the harness detects
divergence rather than silently comparing nothing.

151 tests run against PostgreSQL 16, PostgreSQL 17 and SQL Server 2022.

---

## Limitations

- `IDbCommandInterceptor` does not fire for `COPY` / `SqlBulkCopy` paths — these are not
  `DbCommand`s. EF.Bulk emits [its own events](#diagnostics) instead. `ISaveChangesInterceptor` is
  unaffected.
- JSON-column and stored-procedure-mapped writes always take the stock EF path.
- The explicit API cannot read shadow properties — it works from your objects, and there is no entry
  to read them from. Use `SaveChanges()` for those entity types.
- `IncludeGraph()` rejects two entity types that reference each other; breaking such a cycle needs a
  second pass that `SaveChanges()` performs and this does not.
- Sequence block reservation consumes values on rollback — the same behaviour as stock EF, since
  sequence allocation is non-transactional.

## Sample

A runnable walkthrough of every operation lives in `samples/EF.Bulk.Sample`. It starts its own
PostgreSQL in Docker, so it needs no setup:

```bash
dotnet run --project samples/EF.Bulk.Sample
dotnet run --project samples/EF.Bulk.Sample -- "Host=localhost;Database=shop;Username=postgres;Password=postgres"
```

## License

MIT
