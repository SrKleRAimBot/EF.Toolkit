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

The two modes have genuinely different performance ceilings, and it is worth knowing which one
your workload needs.

**Transparent mode** replaces how EF *executes* a batch, but everything EF does before that still
happens: change detection, building a modification command per row, and the dependency ordering
that makes foreign keys safe. Measured on 5,000 rows, that pipeline is around 70% of the total
time on PostgreSQL — so even an instantaneous write would cap the gain near 2x. It is free to turn
on and it never changes results, but it is not where large numbers come from.

**The explicit API** skips that pipeline entirely — no change detection, no modification commands,
no dependency graph — and reads values straight off your objects through compiled accessors. The
trade is that you take responsibility for ordering: insert principals before their dependents.

Inserting 5,000 rows, one table, server-generated keys:

| | PostgreSQL 16 | SQL Server 2022 |
| --- | --- | --- |
| `SaveChanges()`, stock EF | 273 ms | 358 ms |
| `SaveChanges()`, EF.Bulk | 181 ms — **1.5x** | 215 ms — **1.7x** |
| `BulkInsertAsync` | 31 ms — **8.9x** | 134 ms — **2.7x** |

Local Docker, single connection; treat them as shape rather than absolutes. SQL Server was measured
under arm64 emulation, and its figures are additionally held back by the staging table an `IDENTITY`
column requires — PostgreSQL reserves sequence values up front instead and needs no staging.

## Limitations

- `IDbCommandInterceptor` does not fire for `COPY` / `SqlBulkCopy` paths — these are not
  `DbCommand`s. EF.Bulk emits equivalent `DiagnosticSource` events instead.
  `ISaveChangesInterceptor` is unaffected.
- JSON-column and stored-procedure-mapped writes always take the stock EF path.
- Sequence block reservation consumes values on rollback — the same behaviour as stock EF, since
  sequence allocation is non-transactional.

## License

MIT
