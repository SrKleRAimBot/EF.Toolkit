# EF.Toolkit.Audit.Bulk

The bridge between [EF.Toolkit.Audit](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md)
and [EF.Toolkit.Bulk](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md). Install it
only if you use both.

It closes one gap: the **explicit** bulk API bypasses the change tracker — that is where its
advantage comes from — so no `SaveChanges` interceptor ever sees it, and an audit trail that relies
on one silently misses every `BulkInsertAsync` in the codebase.

## Install

```bash
dotnet add package EF.Toolkit.Audit.Bulk
```

You will already have a provider package for each half — this one is provider-neutral.

## Setup

One extra line, after both halves are registered:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseNpgsql(connectionString)
    .UseBulkOperations()
    .UseAuditing(a => a.Schema("audit"))
    .UseBulkAuditing());          // this package
```

It throws at startup if either half is missing, so it cannot be forgotten quietly.

## What changes

- `BulkInsert` / `Update` / `Delete` / `Merge` / `Synchronize` produce audit entries, inside the
  operation's own transaction and after generated keys have landed.
- Those entries are written through a bulk copy once there are enough of them, because auditing a
  hundred-thousand-row operation produces a hundred thousand entries.

**Transparent mode needs none of this.** `UseBulkOperations()` replaces a service *below*
`SaveChanges`, so an accelerated save is already audited by `UseAuditing()` alone.

### Before-images

A detached object carries no earlier state, so an update or delete reads the rows it is about to
change first — one indexed read joined to the staging table the operation already built, inside the
same transaction, taking the row locks the write is about to take anyway.

That is what lets a bulk-updated row's entry carry the same old-to-new diff a `SaveChanges`-updated
row's does. It also settles a merge's insert-versus-update split per row, and captures the rows a
synchronise's delete arm is about to remove — which correspond to nothing the caller passed in and
would otherwise vanish without trace.

Opt out per call, knowing what it costs:

```csharp
await context.BulkUpdateAsync(orders, o => o.WithoutBeforeImages());   // new values only
await context.BulkDeleteAsync(orders, o => o.WithoutObservers());      // no entries at all
```

A merge or synchronise refuses `WithoutBeforeImages()`, because without them there is no way to say
which rows it inserted and which it updated.

## How small it is

Neither core package depends on the other. This one is written entirely against public seams they
each expose for their own reasons:

- **`IBulkWriteObserver`** (from `EF.Toolkit.Bulk`) sees the entities, values and before-images of an
  explicit bulk operation, inside its transaction — equally useful for an outbox or a projection.
- **`IAuditEntryFactory`** (from `EF.Toolkit.Audit`) turns a described change into audit entries, so
  any capture path produces entries identical to the change tracker's.
- **`IAuditBatchWriter`** (from `EF.Toolkit.Audit`) is an optional faster path for writing many
  entries at once.

This package implements the first in terms of the second, and the third in terms of
`BulkInsertAsync`. That is the whole of the integration.

## Correctness

The differential harness runs the same logical change twice — once through `SaveChanges()`, once
through the explicit bulk API — and requires the resulting audit entries to be identical. Anything
less would make an audited bulk write a second-class record.

## Full documentation

The [EF.Toolkit.Audit documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/audit.md)
covers registration, actors, multi-tenancy, the payload and the audit table.

## License

MIT
