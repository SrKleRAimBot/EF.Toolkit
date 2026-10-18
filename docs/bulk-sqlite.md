# EF.Toolkit.Bulk.Sqlite

The SQLite provider for [EF.Toolkit.Bulk](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).
Install this package rather than the core one: it brings the core with it and supplies the executor
that does the actual work on SQLite.

It depends on `Microsoft.EntityFrameworkCore.Sqlite.Core`, not on the bundle: which native SQLite an
application ships is the application's decision.

## Install

```bash
dotnet add package EF.Toolkit.Bulk.Sqlite
```

## Setup

One call, after the provider:

```csharp
services.AddDbContext<AppDb>(o => o
    .UseSqlite(connectionString)
    .UseBulkOperations());
```

If you reference more than one provider package, `UseBulkOperations` is ambiguous — use
`UseSqliteBulk()`.

## What this package provides

| | How it runs on SQLite |
| --- | --- |
| Insert | one prepared `INSERT`, bound and stepped once per row |
| Generated values | `RETURNING`, per row — keys, defaults and computed columns alike |
| Update / delete | one prepared `UPDATE` / `DELETE` per operation, its affected-row count checked per row |
| Upsert | a prepared `UPDATE` located by the match columns, then a prepared `INSERT` for each row it did not find |
| Synchronize | the upsert, plus `DELETE … WHERE NOT EXISTS` against a temporary table of the match values |

### Why there is no bulk protocol

SQLite runs inside the application's process. A statement costs a function call, not a network
round trip, so there is no round trip for a bulk protocol to save. What makes a large `SaveChanges`
slow on SQLite is everything EF Core does *around* each statement — build a command, generate its
SQL, create its parameters, propagate its results through the change tracker. One prepared
statement reused for the whole set skips all of it: the SQL is compiled once, and each row is a bind
and a step, inside the surrounding transaction.

Every value is bound through the column's own EF type mapping, so a `Guid`, a `decimal` or a
`DateTimeOffset` lands in exactly the text form `SaveChanges` would have written — the forms EF's own
queries then compare against.

That shape also makes this the most capable of the executors. `RETURNING` answers for exactly the
row just written, so every kind of generated value correlates with no staging and no ordinals, and
every affected-row count is per row, so a concurrency conflict names its entity exactly.

### Why the upsert is not `ON CONFLICT DO UPDATE`

A conflict in `INSERT … ON CONFLICT` is a clash on *any* unique index or the primary key, not on the
columns you chose to match on, and the statement needs a unique index over the match columns to
exist at all. Update-then-insert has neither problem, costs SQLite nothing extra — both statements
are prepared once — and reports the insert-versus-update split exactly. SQLite admits one writer at
a time, so no other connection can insert the row in between.

A merge whose source holds two rows with the same match values is refused before anything is
written, as the engines with a `MERGE` statement refuse it.

### What to expect from it

The explicit API is where the gain is: measured at around 6x stock `SaveChanges` for a plain insert.
Transparent `SaveChanges` gains less — around 1.2x to 1.5x — because EF Core's own command building
happens before EF.Toolkit.Bulk is handed the batch, and on an in-process engine that work, not the
writing, is most of the cost.

### Limits that come from SQLite

- `RETURNING` arrived in SQLite 3.35. On anything older, a row set that needs a generated value back
  declines to stock EF Core; everything else is unaffected. Every bundled SQLite that EF Core 10
  ships with is newer.
- EF Core's SQLite provider does not take part in `System.Transactions` scopes, so neither can this.
- SQLite cannot `ORDER BY` a `DateTimeOffset` through EF Core, which constrains queries rather than
  writes, but is worth knowing before keying anything on one.

## Supported versions

SQLite 3.35 and later, through `Microsoft.Data.Sqlite`. The differential harness runs against the
SQLite bundled with EF Core on every commit.

## Full documentation

Everything else — transparent versus explicit mode, the explicit API, change tracking, options,
diagnostics, measured performance and limitations — is in the
[EF.Toolkit.Bulk documentation](https://github.com/SrKleRAimBot/EF.Toolkit/blob/master/docs/bulk.md).

## License

MIT
