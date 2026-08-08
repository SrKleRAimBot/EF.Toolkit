# EF.Bulk

High-performance bulk operations for [EF Core](https://learn.microsoft.com/ef/core) — an
open-source alternative to the commercial [Entity Framework Extensions](https://entityframework-extensions.net/).

EF.Bulk **extends** EF Core from the outside. It is not a fork, and it does not bundle EF — you
install it alongside whatever EF Core version your app already uses.

- **Transparent.** `SaveChanges()` gets dramatically faster with no call-site changes.
- **Explicit when you want it.** `BulkInsert` / `BulkUpdate` / `BulkMerge` / `BulkDelete` /
  `BulkSynchronize` for large detached sets.
- **Change tracking preserved.** Store-generated keys are propagated and entities end in the same
  state stock EF would leave them in.
- **Correct by construction.** Foreign-key ordering is inherited from EF's own dependency
  analysis; anything EF.Bulk cannot accelerate falls back to stock EF rather than changing results.

> **Status: pre-release.** Under active development toward `10.0.0`.

## Install

```bash
dotnet add package EF.Bulk.PostgreSQL   # or EF.Bulk.SqlServer
```

| Package | EF Core | TFM |
| --- | --- | --- |
| `EF.Bulk 10.x` | 10.x | `net10.0` |
| `EF.Bulk 9.x` | 9.x | `net8.0` |

## Setup

One line:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseBulkOperations());
```

Optional tuning:

```csharp
.UseBulkOperations(b => b
    .Threshold(100)                                // rows in a partition before bulk engages
    .MaxBatchSize(50_000)
    .KeyAllocation(KeyAllocation.ReserveBlocks)    // or .Staging
    .OnUnsupported(Unsupported.FallBack))          // or .Throw, to assert the fast path in CI
```

## Transparent mode

No code change at the call site:

```csharp
context.AddRange(orders);           // 50k orders, 200k lines
await context.SaveChangesAsync();   // COPY / SqlBulkCopy, FK-ordered, keys propagated
```

## Explicit API

```csharp
BulkResult r = await context.BulkInsertAsync(orders);
await context.BulkUpdateAsync(orders);   // matches on the primary key
await context.BulkDeleteAsync(orders);   // reads only the keys

// Upsert. The database decides insert-versus-update per row, so there is no read-then-write race.
var m = await context.BulkMergeAsync(customers, o => o.MatchOn(c => c.Email));
Console.WriteLine($"{m.Inserted} new, {m.Updated} existing");

// Make the table match this list exactly — including deleting anything absent from it.
await context.BulkSynchronizeAsync(customers, o => o.MatchOn(c => c.Email));

await context.BulkSaveChangesAsync();    // synonym for SaveChangesAsync once EF.Bulk is enabled
```

With options:

```csharp
await context.BulkInsertAsync(orders, o => o
    .BatchSize(20_000)
    .Track()
    .Timeout(TimeSpan.FromMinutes(5))
    .OnProgress(p => log.LogInformation("{Done}/{Total}", p.Completed, p.Total)));
```

Sync overloads exist for each, and `BulkInsertAsync` also accepts an `IAsyncEnumerable<T>`.

`BulkUpdateAsync` writes **every** non-key column, not just the ones you changed — there is no
change tracking involved to tell it otherwise. A row that has gone missing raises
`DbUpdateConcurrencyException`, as it would under `SaveChanges()`.

`BulkMergeAsync` matches on the primary key unless you say otherwise, and the match columns must
have a unique index — `ON CONFLICT` needs one to define what a conflict *is*, and without one a
`MERGE` would happily match several rows at once. Store-generated keys are populated on the
entities that turned out to be inserts.

`BulkSynchronizeAsync` **deletes every row the source does not contain** — that is what makes it a
synchronise rather than a merge, and it is easy to trigger with a partial list by accident. It
refuses an empty source rather than emptying the table, and runs in one transaction so the table is
never seen half-synchronised.

`BulkSaveChangesAsync` is a synonym for `SaveChangesAsync`: once `UseBulkOperations()` is applied
every save already goes through EF.Bulk. It exists because it is the name people look for.

### Writing a whole graph

By default you order writes yourself. `IncludeGraph()` does it for you: it follows navigations from
the entities you pass in, orders the entity types so principals come first, and fills in each
foreign key from its navigation once the principal has a key — the job change tracking would
normally do.

```csharp
foreach (var customer in customers)
{
    var order = new Order { Customer = customer, ... };   // no CustomerId
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

### Change tracking

Store-generated keys are always written back onto your objects. Tracking is separate and, for the
explicit API, opt-in:

```csharp
await ctx.BulkInsertAsync(orders);   // detached input
orders[0].Id;                        // populated
ctx.Entry(orders[0]).State;          // Detached

await ctx.BulkInsertAsync(orders, o => o.Track());
ctx.Entry(orders[0]).State;          // Unchanged
```

Entities the context is *already* tracking are always reconciled to `Unchanged` after a bulk
write, so a later `SaveChanges()` cannot re-insert them.

## What to expect from each mode

Inserting one table with server-generated keys into PostgreSQL 16. BenchmarkDotNet, five
iterations after warmup, Docker on an M-series Mac — read the ratios, not the absolute times.

| Rows | | Time | vs stock | Allocated |
| ---: | --- | ---: | ---: | ---: |
| 1,000 | `SaveChanges()`, stock EF | 49 ms | — | 8.5 MB |
| | `SaveChanges()`, EF.Bulk | 31 ms | **1.6x** | 5.0 MB |
| | `BulkInsertAsync` | 8 ms | **6.2x** | 0.3 MB |
| 10,000 | `SaveChanges()`, stock EF | 363 ms | — | 77.6 MB |
| | `SaveChanges()`, EF.Bulk | 278 ms | **1.3x** | 47.3 MB |
| | `BulkInsertAsync` | 44 ms | **8.3x** | 2.1 MB |
| 100,000 | `SaveChanges()`, stock EF | 2,464 ms | — | 766 MB |
| | `SaveChanges()`, EF.Bulk | 526 ms | **4.7x** | 466 MB |
| | `BulkInsertAsync` | 233 ms | **10.6x** | 16.6 MB |

Update, delete and upsert at 10,000 rows. Seeding and loading happen outside the measurement, so
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
the existing rows, decide per item whether to add or update, save. Besides being slower it also
races — another writer can insert between the read and the write — which the merge cannot, because
the database makes the decision.

Reproduce with `dotnet run --project tests/EF.Bulk.Benchmarks -c Release -- --filter "*"`.

## Sample

A runnable walkthrough of every operation lives in `samples/EF.Bulk.Sample`. It starts its own
PostgreSQL in Docker, so it needs no setup:

```bash
dotnet run --project samples/EF.Bulk.Sample
dotnet run --project samples/EF.Bulk.Sample -- "Host=localhost;Database=shop;Username=postgres;Password=postgres"
```

**Transparent mode** replaces how EF executes a batch, but everything before that still happens:
change detection, a modification command per row, and the dependency ordering that keeps foreign
keys safe. That fixed cost is why the gain is modest on small saves — but it does not stay modest.
At 100,000 rows stock EF allocates 766 MB and starts collecting gen-2, and avoiding most of that
work is worth 4.7x. Turning it on is free and never changes results.

**The explicit API** skips that pipeline entirely and reads values straight off your objects
through compiled accessors, in exchange for you ordering principals before dependents (or using
`IncludeGraph()`). It is faster at every size, and the memory difference is the more striking
number: **46x less allocated at 100,000 rows**, with no gen-2 collections at all. If you are
loading data rather than saving a graph you have been working with, use it.

### Concurrency tokens

Optimistic concurrency works under transparent mode. The value the row was loaded with is staged to
locate it, the new value is staged separately to assign, and anything the database regenerates is
read back — so a token column, which is written *and* used to find the row, gets both of its values
into the same statement.

A row that no longer matches raises `DbUpdateConcurrencyException` naming the affected entities, as
it would under stock EF. That takes some doing: a bulk statement reports one affected-row count for
the whole set, so each statement returns the keys it actually touched and anything missing from
that set is a conflict.

## Limitations

- `IDbCommandInterceptor` does not fire for `COPY` / `SqlBulkCopy` paths — these are not
  `DbCommand`s. EF.Bulk emits equivalent `DiagnosticSource` events instead.
  `ISaveChangesInterceptor` is unaffected.
- JSON-column and stored-procedure-mapped writes always take the stock EF path.
- Sequence block reservation consumes values on rollback — the same behaviour as stock EF, since
  sequence allocation is non-transactional.

## License

MIT
